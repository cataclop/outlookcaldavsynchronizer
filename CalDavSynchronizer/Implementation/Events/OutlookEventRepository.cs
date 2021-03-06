// This file is Part of CalDavSynchronizer (http://outlookcaldavsynchronizer.sourceforge.net/)
// Copyright (c) 2015 Gerhard Zehetbauer
// Copyright (c) 2015 Alexander Nimmervoll
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using CalDavSynchronizer.Contracts;
using CalDavSynchronizer.Implementation.Common;
using CalDavSynchronizer.Implementation.ComWrappers;
using CalDavSynchronizer.Implementation.TimeRangeFiltering;
using GenSync;
using GenSync.EntityRepositories;
using GenSync.Logging;
using log4net;
using Microsoft.Office.Interop.Outlook;
using Exception = System.Exception;

namespace CalDavSynchronizer.Implementation.Events
{
  public class OutlookEventRepository : IEntityRepository<AppointmentItemWrapper, string, DateTime, IEventSynchronizationContext>
  {
    private static readonly ILog s_logger = LogManager.GetLogger (System.Reflection.MethodInfo.GetCurrentMethod().DeclaringType);

    private readonly NameSpace _mapiNameSpace;
    private readonly string _folderId;
    private readonly string _folderStoreId;
    private readonly IDateTimeRangeProvider _dateTimeRangeProvider;
    private readonly EventMappingConfiguration _configuration;
    private readonly IDaslFilterProvider _daslFilterProvider;
    private readonly IInvitationChecker _invitationChecker;

    public OutlookEventRepository (
      NameSpace mapiNameSpace, 
      string folderId, 
      string folderStoreId, 
      IDateTimeRangeProvider dateTimeRangeProvider,
      EventMappingConfiguration configuration,
      IDaslFilterProvider daslFilterProvider, 
      IInvitationChecker invitationChecker)
    {
      if (mapiNameSpace == null)
        throw new ArgumentNullException (nameof (mapiNameSpace));
      if (dateTimeRangeProvider == null)
        throw new ArgumentNullException (nameof (dateTimeRangeProvider));
      if (configuration == null)
        throw new ArgumentNullException (nameof (configuration));
      if (daslFilterProvider == null)
        throw new ArgumentNullException (nameof (daslFilterProvider));
      if (invitationChecker == null)
        throw new ArgumentNullException (nameof (invitationChecker));

      _mapiNameSpace = mapiNameSpace;
      _folderId = folderId;
      _folderStoreId = folderStoreId;
      _dateTimeRangeProvider = dateTimeRangeProvider;
      _configuration = configuration;
      _daslFilterProvider = daslFilterProvider;
      _invitationChecker = invitationChecker;
    }

    private const string c_entryIdColumnName = "EntryID";


    private GenericComObjectWrapper<Folder> CreateFolderWrapper ()
    {
      return GenericComObjectWrapper.Create ((Folder) _mapiNameSpace.GetFolderFromID (_folderId, _folderStoreId));
    }

    public Task<IReadOnlyList<EntityVersion<string, DateTime>>> GetVersions (IEnumerable<IdWithAwarenessLevel<string>> idsOfEntitiesToQuery, IEventSynchronizationContext context)
    {
      var result = new List<EntityVersion<string, DateTime>>();

      foreach (var id in idsOfEntitiesToQuery)
      {
        var appointment = _mapiNameSpace.GetEntryOrNull<AppointmentItem> (id.Id, _folderId, _folderStoreId);
        if (appointment != null)
        {
          try
          {
            if (id.IsKnown || DoesMatchCategoryCriterion (appointment))
            {
              result.Add (EntityVersion.Create (id.Id, appointment.LastModificationTime));
              context.AnnounceAppointment (appointment);
            }
          }
          finally
          {
              Marshal.FinalReleaseComObject (appointment);
          }
        }
      }

      return Task.FromResult<IReadOnlyList<EntityVersion<string, DateTime>>> ( result);
    }
    
    private bool DoesMatchCategoryCriterion (AppointmentItem item)
    {
      if (!_configuration.UseEventCategoryAsFilter)
        return true;

      var categoryCsv = item.Categories;

      if (string.IsNullOrEmpty (categoryCsv))
        return _configuration.InvertEventCategoryFilter;

      var found = item.Categories
          .Split (new[] { CultureInfo.CurrentCulture.TextInfo.ListSeparator }, StringSplitOptions.RemoveEmptyEntries)
          .Select (c => c.Trim())
          .Any (c => c == _configuration.EventCategory);
      return _configuration.InvertEventCategoryFilter ? !found : found;
    }

    public Task<IReadOnlyList<EntityVersion<string, DateTime>>> GetAllVersions (IEnumerable<string> idsOfknownEntities, IEventSynchronizationContext context)
    {
      return GetAll (
          idsOfknownEntities,
          context,
          a =>
          {
            context.AnnounceAppointment (a);
            return new EntityVersion<string, DateTime> (a.EntryID, a.LastModificationTime);
          });
    }

    private Task<IReadOnlyList<T>> GetAll<T> (IEnumerable<string> idsOfknownEntities, IEventSynchronizationContext context, Func<AppointmentItem,T> selector)
      where T : IEntity<string>
    {
      var range = _dateTimeRangeProvider.GetRange ();

      List<T> events;
      using (var calendarFolderWrapper = CreateFolderWrapper ())
      {
        bool isInstantSearchEnabled = false;

        try
        {
          using (var store = GenericComObjectWrapper.Create (calendarFolderWrapper.Inner.Store))
          {
            if (store.Inner != null)
              isInstantSearchEnabled = store.Inner.IsInstantSearchEnabled;
          }
        }
        catch (COMException)
        {
          s_logger.Info ("Can't access IsInstantSearchEnabled property of store, defaulting to false.");
        }

        // Table Filtering in the MSDN: https://msdn.microsoft.com/EN-US/library/office/ff867581.aspx
        var filterBuilder = new StringBuilder (_daslFilterProvider.GetAppointmentFilter (isInstantSearchEnabled));

        if (range.HasValue)
          filterBuilder.AppendFormat (" And \"urn:schemas:calendar:dtstart\" < '{0}' And \"urn:schemas:calendar:dtend\" > '{1}'", ToOutlookDateString (range.Value.To), ToOutlookDateString (range.Value.From));
        if (_configuration.UseEventCategoryAsFilter)
        {
          AddCategoryFilter (filterBuilder, _configuration.EventCategory, _configuration.InvertEventCategoryFilter);
        }

        s_logger.InfoFormat ("Using Outlook DASL filter: {0}", filterBuilder.ToString ());

        events = QueryFolder (_mapiNameSpace, calendarFolderWrapper, filterBuilder, selector);
      }

      if (_configuration.UseEventCategoryAsFilter)
      {
        var knownEntitesThatWereFilteredOut = idsOfknownEntities.Except (events.Select (e => e.Id));
        events.AddRange (
            knownEntitesThatWereFilteredOut
                .Select (id => _mapiNameSpace.GetEntryOrNull<AppointmentItem> (id, _folderId, _folderStoreId))
                .Where (i => i != null)
                .ToSafeEnumerable ()
                .Select (selector));
      }

      return Task.FromResult<IReadOnlyList<T>> (events);
    }


    public static void AddCategoryFilter (StringBuilder filterBuilder, string category, bool negate)
    {
      var negateFilter = negate ? "Not" : "";
      filterBuilder.AppendFormat (" And "+ negateFilter + "(\"urn:schemas-microsoft-com:office:office#Keywords\" = '{0}')", category.Replace ("'","''"));
    }

    public static List<EntityVersion<string, DateTime>> QueryFolder (
        NameSpace session,
        GenericComObjectWrapper<Folder> calendarFolderWrapper,
        StringBuilder filterBuilder)
    {
      return QueryFolder (
          session,
          calendarFolderWrapper,
          filterBuilder,
          a => new EntityVersion<string, DateTime> (a.EntryID, a.LastModificationTime));
    }

    static List<T> QueryFolder<T> (
        NameSpace session,
        GenericComObjectWrapper<Folder> calendarFolderWrapper,
        StringBuilder filterBuilder,
        Func<AppointmentItem, T> selector)
      where T : IEntity<string>
    {
      var events = new List<T>();

      using (var tableWrapper = GenericComObjectWrapper.Create (
          calendarFolderWrapper.Inner.GetTable (filterBuilder.ToString())))
      {
        var table = tableWrapper.Inner;
        table.Columns.RemoveAll();
        table.Columns.Add (c_entryIdColumnName);

        var storeId = calendarFolderWrapper.Inner.StoreID;

        while (!table.EndOfTable)
        {
          var row = table.GetNextRow();
          var entryId = (string) row[c_entryIdColumnName];
          try
          {
            using (var appointmentWrapper = GenericComObjectWrapper.Create ((AppointmentItem) session.GetItemFromID (entryId, storeId)))
            {
              events.Add (selector (appointmentWrapper.Inner));
            }
          }
          catch (COMException ex)
          {
            s_logger.Error ("Could not fetch AppointmentItem, skipping.", ex);
          }
        }
      }
      return events;
    }

    private static readonly CultureInfo _currentCultureInfo = CultureInfo.CurrentCulture;

    private string ToOutlookDateString (DateTime value)
    {
      return value.ToString ("g", _currentCultureInfo);
    }

#pragma warning disable 1998
    public async Task<IReadOnlyList<EntityWithId<string, AppointmentItemWrapper>>> Get (ICollection<string> ids, ILoadEntityLogger logger, IEventSynchronizationContext context)
#pragma warning restore 1998
    {
      return ids
          .Select (id => EntityWithId.Create (
              id,
              new AppointmentItemWrapper (
                  (AppointmentItem) _mapiNameSpace.GetItemFromID (id, _folderStoreId),
                  entryId => (AppointmentItem) _mapiNameSpace.GetItemFromID (entryId, _folderStoreId))))
          .ToArray();
    }

    public async Task VerifyUnknownEntities (Dictionary<string, DateTime> unknownEntites, IEventSynchronizationContext context)
    {
      foreach (var unknownEntity in await Get (unknownEntites.Keys, NullLoadEntityLogger.Instance, context))
      {
        using (unknownEntity.Entity)
        {
          if (_invitationChecker.IsMeetingInvitationFromServerIdentity (unknownEntity.Entity.Inner))
          {
            var subjectForLogging = s_logger.IsDebugEnabled ? "'" + unknownEntity.Entity.Inner.Subject + "' " : string.Empty;
            s_logger.Info ($"Deleting meeting invitation {subjectForLogging}('{unknownEntity.Id}') from server identity.");

            unknownEntites.Remove (unknownEntity.Id);
            context.AnnounceAppointmentDeleted (unknownEntity.Entity.Inner);
            try
            {
              unknownEntity.Entity.Inner.Delete();
            }
            catch (COMException ex)
            {
              s_logger.Warn ("Could not delete AppointmentItem", ex);
            }
          }
        }
      }
    }

    public void Cleanup (IReadOnlyDictionary<string, AppointmentItemWrapper> entities)
    {
      foreach (var appointmentItemWrapper in entities.Values)
        appointmentItemWrapper.Dispose();
    }

    public async Task<EntityVersion<string, DateTime>> TryUpdate (
        string entityId,
        DateTime entityVersion,
        AppointmentItemWrapper entityToUpdate,
        Func<AppointmentItemWrapper, Task<AppointmentItemWrapper>> entityModifier,
        IEventSynchronizationContext context)
    {
      entityToUpdate = await entityModifier (entityToUpdate);
      entityToUpdate.Inner.Save();
      context.AnnounceAppointment (entityToUpdate.Inner);
      return new EntityVersion<string, DateTime> (entityToUpdate.Inner.EntryID, entityToUpdate.Inner.LastModificationTime);
    }

    public Task<bool> TryDelete (string entityId, DateTime version, IEventSynchronizationContext context)
    {
      var entityWithId = Get (new[] { entityId }, NullLoadEntityLogger.Instance, context).Result.SingleOrDefault();
      if (entityWithId == null)
        return Task.FromResult (true);

      using (var appointment = entityWithId.Entity)
      {
        context.AnnounceAppointmentDeleted (appointment.Inner);
        appointment.Inner.Delete();
      }
      return Task.FromResult (true);
    }

    public async Task<EntityVersion<string, DateTime>> Create (Func<AppointmentItemWrapper, Task<AppointmentItemWrapper>> entityInitializer, IEventSynchronizationContext context)
    {
      AppointmentItemWrapper newAppointmentItemWrapper;

      using (var folderWrapper = CreateFolderWrapper())
      {
        newAppointmentItemWrapper = new AppointmentItemWrapper (
            (AppointmentItem) folderWrapper.Inner.Items.Add (OlItemType.olAppointmentItem),
            entryId => (AppointmentItem) _mapiNameSpace.GetItemFromID (entryId, _folderStoreId));
      }

      using (newAppointmentItemWrapper)
      {
        using (var initializedWrapper = await entityInitializer (newAppointmentItemWrapper))
        {
          initializedWrapper.SaveAndReload();
          context.AnnounceAppointment (initializedWrapper.Inner);
          var result = new EntityVersion<string, DateTime> (initializedWrapper.Inner.EntryID, initializedWrapper.Inner.LastModificationTime);
          return result;
        }
      }
    }

    public static AppointmentItemWrapper CreateNewAppointmentForTesting (MAPIFolder calendarFolder, NameSpace mapiNamespace, string folderStoreId)
    {
      return new AppointmentItemWrapper ((AppointmentItem) calendarFolder.Items.Add (OlItemType.olAppointmentItem), entryId => (AppointmentItem) mapiNamespace.GetItemFromID (entryId, folderStoreId));
    }


    public static AppointmentItemWrapper GetOutlookEventForTesting (string id, NameSpace mapiNamespace, string folderStoreId)
    {
      return new AppointmentItemWrapper (
          (AppointmentItem) mapiNamespace.GetItemFromID (id, folderStoreId),
          entryId => (AppointmentItem) mapiNamespace.GetItemFromID (id, folderStoreId));
    }
  }
}