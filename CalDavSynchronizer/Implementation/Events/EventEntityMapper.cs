﻿// This file is Part of CalDavSynchronizer (http://outlookcaldavsynchronizer.sourceforge.net/)
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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CalDavSynchronizer.Contracts;
using CalDavSynchronizer.DDayICalWorkaround;
using CalDavSynchronizer.Implementation.ComWrappers;
using CalDavSynchronizer.Implementation.Common;
using CalDavSynchronizer.Implementation.TimeZones;
using DDay.iCal;
using GenSync.EntityMapping;
using GenSync.Logging;
using log4net;
using Microsoft.Office.Interop.Outlook;
using NodaTime;
using Exception = Microsoft.Office.Interop.Outlook.Exception;
using Period = DDay.iCal.Period;
using RecurrencePattern = DDay.iCal.RecurrencePattern;

namespace CalDavSynchronizer.Implementation.Events
{
  public class EventEntityMapper : IEntityMapper<AppointmentItemWrapper, IICalendar>
  {
    private static readonly ILog s_logger = LogManager.GetLogger (MethodInfo.GetCurrentMethod().DeclaringType);

    private const string PR_SENDER_NAME = "http://schemas.microsoft.com/mapi/proptag/0x0C1A001E";
    private const string PR_SENDER_EMAIL_ADDRESS = "http://schemas.microsoft.com/mapi/proptag/0x0C1F001E";
    private const string PR_SENT_REPRESENTING_NAME = "http://schemas.microsoft.com/mapi/proptag/0x0042001E";
    private const string PR_SENT_REPRESENTING_EMAIL_ADDRESS = "http://schemas.microsoft.com/mapi/proptag/0x0065001E";
    private const string PR_SENT_REPRESENTING_ADDRTYPE = "http://schemas.microsoft.com/mapi/proptag/0x0064001E";
    private const string PR_SENDER_ADDRTYPE = "http://schemas.microsoft.com/mapi/proptag/0x0C1E001E";
    private const string PR_SENT_REPRESENTING_ENTRYID = "http://schemas.microsoft.com/mapi/proptag/0x00410102";
    private const string PR_SENDER_ENTRYID = "http://schemas.microsoft.com/mapi/proptag/0x0C190102";

    private readonly int _outlookMajorVersion;

    private readonly string _outlookEmailAddress;
    private readonly string _serverEmailUri;
    private readonly TimeZoneInfo _localTimeZoneInfo;
    private readonly string _localTimeZoneId;
    private readonly ITimeZone _configuredEventTimeZoneOrNull;
    private readonly EventMappingConfiguration _configuration;
    private readonly ITimeZoneCache _timeZoneCache;

    public EventEntityMapper (
        string outlookEmailAddress,
        Uri serverEmailAddress,
        string localTimeZoneId,
        string outlookApplicationVersion,
        ITimeZoneCache timeZoneCache,
        EventMappingConfiguration configuration, 
        ITimeZone configuredEventTimeZoneOrNull)
    {
      _outlookEmailAddress = outlookEmailAddress;
      _configuration = configuration;
      _configuredEventTimeZoneOrNull = configuredEventTimeZoneOrNull;
      _serverEmailUri = serverEmailAddress.ToString();
      _localTimeZoneId = localTimeZoneId;
      _localTimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById (_localTimeZoneId);
      _timeZoneCache = timeZoneCache;

      

      string outlookMajorVersionString = outlookApplicationVersion.Split (new char[] { '.' })[0];
      _outlookMajorVersion = Convert.ToInt32 (outlookMajorVersionString);
    }

    public async Task<IICalendar> Map1To2 (AppointmentItemWrapper sourceWrapper, IICalendar existingTargetCalender, IEntityMappingLogger logger)
    {
      var newTargetCalender = new iCalendar();

      ITimeZone startIcalTimeZone = null;
      ITimeZone endIcalTimeZone = null;

      if (!_configuration.CreateEventsInUTC)
      {
        string startTimeZoneID;
        string endTimeZoneID;

        try
        {
          using (var startTimeZone = GenericComObjectWrapper.Create (sourceWrapper.Inner.StartTimeZone))
          {
            startTimeZoneID = startTimeZone.Inner.ID;
          }
          using (var endTimeZone = GenericComObjectWrapper.Create (sourceWrapper.Inner.EndTimeZone))
          {
            endTimeZoneID = endTimeZone.Inner.ID;
          }

          if (_configuration.UseIanaTz)
          {
            if (_localTimeZoneId == startTimeZoneID && _configuredEventTimeZoneOrNull != null)
            {
              newTargetCalender.TimeZones.Add (_configuredEventTimeZoneOrNull);
              startIcalTimeZone = _configuredEventTimeZoneOrNull;
            }
            else
            {
              var startIanaTzId = TimeZoneMapper.WindowsToIana (startTimeZoneID);
              startIcalTimeZone = await _timeZoneCache.GetByTzIdOrNull (startIanaTzId);
              if (startIcalTimeZone != null)
                newTargetCalender.TimeZones.Add (startIcalTimeZone);
            }
          }
          else
          {
            var startTimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById (startTimeZoneID);
            startIcalTimeZone = iCalTimeZone.FromSystemTimeZone (startTimeZoneInfo, new DateTime (1970, 1, 1), false);
            CalendarDataPreprocessor.FixTimeZoneDSTRRules (startTimeZoneInfo, startIcalTimeZone);
            newTargetCalender.TimeZones.Add (startIcalTimeZone);            
          }

          if (endTimeZoneID != startTimeZoneID)
          {
            if (_configuration.UseIanaTz)
            {
              if (_localTimeZoneId == endTimeZoneID && _configuredEventTimeZoneOrNull != null)
              {
                newTargetCalender.TimeZones.Add (_configuredEventTimeZoneOrNull);
                endIcalTimeZone = _configuredEventTimeZoneOrNull;
              }
              else
              {
                var endIanaTzId = TimeZoneMapper.WindowsToIana (endTimeZoneID);
                endIcalTimeZone = await _timeZoneCache.GetByTzIdOrNull (endIanaTzId);
                if (endIcalTimeZone != null)
                  newTargetCalender.TimeZones.Add (endIcalTimeZone);
              }
            }
            else
            {
              var endTimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById (endTimeZoneID);

              endIcalTimeZone = iCalTimeZone.FromSystemTimeZone (endTimeZoneInfo, new DateTime (1970, 1, 1), false);
              CalendarDataPreprocessor.FixTimeZoneDSTRRules (endTimeZoneInfo, endIcalTimeZone);
              newTargetCalender.TimeZones.Add (endIcalTimeZone);
            }
          }
          else
          {
            endIcalTimeZone = startIcalTimeZone;
          }
        }
        catch (COMException ex)
        {
          s_logger.Warn ("Can't get Timezone of AppointmentItem, using UTC", ex);
          logger.LogMappingWarning ("Can't get Timezone of AppointmentItem, using UTC");
        }
      }

      var existingTargetEvent = existingTargetCalender.Events.FirstOrDefault (e => e.RecurrenceID == null);

      var newTargetEvent = new Event();

      if (existingTargetEvent != null)
        newTargetEvent.UID = existingTargetEvent.UID;
      else if (_configuration.UseGlobalAppointmentID)
        newTargetEvent.UID = sourceWrapper.Inner.GlobalAppointmentID;

      newTargetCalender.Events.Add (newTargetEvent);

      Map1To2 (sourceWrapper.Inner, newTargetEvent, false, startIcalTimeZone, endIcalTimeZone, logger);

      for (int i = 0, newSequenceNumber = existingTargetCalender.Events.Count > 0 ? existingTargetCalender.Events.Max (e => e.Sequence) + 1 : 0;
          i < newTargetCalender.Events.Count;
          i++, newSequenceNumber++)
      {
        newTargetCalender.Events[i].Sequence = newSequenceNumber;
      }

      return newTargetCalender;
    }

    private void Map1To2 (AppointmentItem source, IEvent target, bool isRecurrenceException, ITimeZone startIcalTimeZone, ITimeZone endIcalTimeZone, IEntityMappingLogger logger)
    {
      if (source.AllDayEvent)
      {
        // Outlook's AllDayEvent relates to Start and not not StartUtc!!!
        target.Start = new iCalDateTime (source.Start);
        target.Start.HasTime = false;
        target.End = new iCalDateTime (source.End);
        target.End.HasTime = false;
        target.IsAllDay = true;
      }
      else
      {
        if (_configuration.CreateEventsInUTC || startIcalTimeZone == null || endIcalTimeZone == null)
        {
          target.Start = new iCalDateTime (source.StartUTC) { IsUniversalTime = true };
          target.DTEnd = new iCalDateTime (source.EndUTC) { IsUniversalTime = true };
        }
        else if (_configuration.UseIanaTz) 
        {
          var startInstant = Instant.FromDateTimeUtc (source.Start.ToUniversalTime());
          var startTimeZone = DateTimeZoneProviders.Tzdb[startIcalTimeZone.TZID];
          var zonedStart = startInstant.InZone (startTimeZone);
          target.Start = new iCalDateTime (zonedStart.ToDateTimeUnspecified());
          target.Start.SetTimeZone (startIcalTimeZone);
          var endInstant = Instant.FromDateTimeUtc (source.End.ToUniversalTime());
          var endTimeZone = DateTimeZoneProviders.Tzdb[endIcalTimeZone.TZID];
          var zonedEnd = endInstant.InZone (endTimeZone);
          target.End = new iCalDateTime (zonedEnd.ToDateTimeUnspecified());
          target.End.SetTimeZone (endIcalTimeZone);
        }
        else
        {
          target.Start = new iCalDateTime (source.StartInStartTimeZone);
          target.Start.SetTimeZone (startIcalTimeZone);
          target.DTEnd = new iCalDateTime (source.EndInEndTimeZone);
          target.End.SetTimeZone (endIcalTimeZone);
        }
        target.IsAllDay = false;
      }

      target.Summary = source.Subject;
      if (!string.IsNullOrEmpty (target.Summary) && 
          target.Summary.StartsWith ("Cancelled: "))
        target.Status = EventStatus.Cancelled;

      target.Location = source.Location;

      if (_configuration.MapBody)
        target.Description = source.Body;

      target.Priority = CommonEntityMapper.MapPriority1To2 (source.Importance);


      if (_configuration.MapAttendees)
      {
        bool organizerSet;
        MapAttendees1To2 (source, target, out organizerSet, logger);
        if (!organizerSet)
          MapOrganizer1To2 (source, target, logger);
      }

      if (!isRecurrenceException)
        MapRecurrance1To2 (source, target, startIcalTimeZone, endIcalTimeZone, logger);


      target.Class = CommonEntityMapper.MapPrivacy1To2 (source.Sensitivity, _configuration.MapSensitivityPrivateToClassConfidential);

      MapReminder1To2 (source, target);

      MapCategories1To2 (source, target);

      target.Properties.Add (MapTransparency1To2 (source.BusyStatus));
      target.Properties.Add (MapBusyStatus1To2 (source.BusyStatus));
    }

    private static CalendarProperty MapBusyStatus1To2 (OlBusyStatus value)
    {
      switch (value)
      {
        case OlBusyStatus.olTentative:
          return new CalendarProperty ("X-MICROSOFT-CDO-BUSYSTATUS", "TENTATIVE");                        
        case OlBusyStatus.olOutOfOffice:                       
          return new CalendarProperty ("X-MICROSOFT-CDO-BUSYSTATUS", "OOF");
        case OlBusyStatus.olFree:
          return new CalendarProperty ("X-MICROSOFT-CDO-BUSYSTATUS", "FREE");
        case OlBusyStatus.olWorkingElsewhere:
            return new CalendarProperty ("X-MICROSOFT-CDO-BUSYSTATUS", "WORKINGELSEWHERE");
        case OlBusyStatus.olBusy:
        default:                             
          return new CalendarProperty ("X-MICROSOFT-CDO-BUSYSTATUS", "BUSY");
      }
    }

    private static CalendarProperty MapTransparency1To2 (OlBusyStatus value)
    {
      switch (value)
      {
        case OlBusyStatus.olBusy:
        case OlBusyStatus.olOutOfOffice:
        case OlBusyStatus.olWorkingElsewhere:
          return new CalendarProperty ("TRANSP", "OPAQUE");
        case OlBusyStatus.olTentative:
        case OlBusyStatus.olFree:
          return new CalendarProperty ("TRANSP", "TRANSPARENT");
      }

      throw new NotImplementedException (string.Format ("Mapping for value '{0}' not implemented.", value));
    }


    private static OlBusyStatus MapTransparency2To1 (IEvent source)
    {
      if (source.Properties.ContainsKey ("X-MICROSOFT-CDO-BUSYSTATUS"))
      {
        switch (source.Properties["X-MICROSOFT-CDO-BUSYSTATUS"].Value.ToString())
        {
          case "WORKINGELSEWHERE":
            return OlBusyStatus.olWorkingElsewhere;
          case "FREE":
            return OlBusyStatus.olFree;
          case "TENTATIVE":
            return OlBusyStatus.olTentative;
          case "OOF":
            return OlBusyStatus.olOutOfOffice;
          case "BUSY":
          default:
            return OlBusyStatus.olBusy;
        }
      }
      else
      {
        if (source.Transparency == TransparencyType.Opaque)
          return OlBusyStatus.olBusy;
        else return OlBusyStatus.olFree;
      }
    }

    private void MapCategories1To2 (AppointmentItem source, IEvent target)
    {
      if (!string.IsNullOrEmpty (source.Categories))
      {
        var useEventCategoryAsFilter = _configuration.UseEventCategoryAsFilter;

        var sourceCategories = CommonEntityMapper.SplitCategoryString (source.Categories)
                .Where (c => !useEventCategoryAsFilter || c != _configuration.EventCategory);

        foreach (var sourceCategory in sourceCategories)
        {
          target.Categories.Add (sourceCategory);
        }
      }
    }

    private void MapReminder1To2 (AppointmentItem source, IEvent target)
    {
      if (_configuration.MapReminder == ReminderMapping.@false)
        return;

      if (source.ReminderSet)
      {
        var reminderRelativeToStart = TimeSpan.FromMinutes (-source.ReminderMinutesBeforeStart);

        if (_configuration.MapReminder == ReminderMapping.JustUpcoming
            && source.StartUTC.Add (reminderRelativeToStart) <= DateTime.UtcNow)
          return;

        var trigger = new Trigger (reminderRelativeToStart);

        target.Alarms.Add (
          new Alarm()
          {
            Description = "This is an event reminder"
          }
          );
        // Fix DDay.iCal TimeSpan 0 serialization
        if (reminderRelativeToStart == TimeSpan.Zero)
        {
          target.Alarms[0].Properties.Add (new CalendarProperty ("TRIGGER", "-P0D"));
        }
        else
        {
          target.Alarms[0].Trigger = trigger;
        }
        // Fix for google, since Google wants ACTION property DISPLAY in uppercase
        var actionProperty = new CalendarProperty ("ACTION", "DISPLAY");
        target.Alarms[0].Properties.Add (actionProperty);

      }
    }
    

    private void MapReminder2To1 (IEvent source, AppointmentItem target, IEntityMappingLogger logger)
    {
      if (_configuration.MapReminder == ReminderMapping.@false)
      {
        target.ReminderSet = false;
        return;
      }

      if (source.Alarms.Count == 0)
      {
        target.ReminderSet = false;
        return;
      }

      if (source.Alarms.Count > 1)
      {
        s_logger.WarnFormat ("Event '{0}' contains multiple alarms. Ignoring all except first.", source.Url);
        logger.LogMappingWarning ("Event contains multiple alarms. Ignoring all except first.");
      }

      var alarm = source.Alarms[0];

      if (alarm.Trigger == null)
      {
        s_logger.WarnFormat ("Event '{0}' contains non RFC-conform alarm. Ignoring alarm.", source.Url);
        logger.LogMappingWarning ("Event contains non RFC-conform alarm. Ignoring alarm.");
        target.ReminderSet = false;
        return;
      }

      if (!(alarm.Trigger.IsRelative
            && alarm.Trigger.Related == TriggerRelation.Start
            && alarm.Trigger.Duration.HasValue
            && alarm.Trigger.Duration <= TimeSpan.Zero))
      {
        s_logger.WarnFormat ("Event '{0}' alarm is not relative before event start. Ignoring.", source.Url);
        logger.LogMappingWarning ("Alarm is not relative before event start. Ignoring.");
        target.ReminderSet = false;
        return;
      }

      if (_configuration.MapReminder == ReminderMapping.JustUpcoming
          && target.StartUTC.Add (alarm.Trigger.Duration.Value) <= DateTime.UtcNow)
      {
        target.ReminderSet = false;
        return;
      }

      target.ReminderSet = true;
      target.ReminderMinutesBeforeStart = -(int) alarm.Trigger.Duration.Value.TotalMinutes;
    }

    private string MapParticipation1To2 (OlResponseStatus value)
    {
      switch (value)
      {
        case OlResponseStatus.olResponseAccepted:
          return "ACCEPTED";
        case OlResponseStatus.olResponseDeclined:
          return "DECLINED";
        case OlResponseStatus.olResponseOrganized:
          return "ACCEPTED";
        case OlResponseStatus.olResponseTentative:
          return "TENTATIVE";
        case OlResponseStatus.olResponseNone:
        case OlResponseStatus.olResponseNotResponded:
        default:
          return "NEEDS-ACTION";
      }
    }

    private OlResponseStatus MapParticipation2To1 (string value)
    {
      switch (value)
      {
        case "NEEDS-ACTION":
          return OlResponseStatus.olResponseNotResponded;
        case "ACCEPTED":
          return OlResponseStatus.olResponseAccepted;
        case "DECLINED":
          return OlResponseStatus.olResponseDeclined;
        case "TENTATIVE":
          return OlResponseStatus.olResponseTentative;
        case "DELEGATED":
          return OlResponseStatus.olResponseNotResponded;
        case null:
          return OlResponseStatus.olResponseNone;
        // according to the RFC 5545 not recognized values must be treated the same way as NEEDS-ACTION
        default:
          return OlResponseStatus.olResponseNotResponded;
      }
    }


    private OlMeetingResponse? MapParticipation2ToMeetingResponse (string value)
    {
      switch (value)
      {
        case "ACCEPTED":
          return OlMeetingResponse.olMeetingAccepted;
        case "DECLINED":
          return OlMeetingResponse.olMeetingDeclined;
        case "TENTATIVE":
          return OlMeetingResponse.olMeetingTentative;
        case "NEEDS-ACTION":
        case "DELEGATED":
        // according to the RFC 5545 not recognized values must be treated the same way as NEEDS-ACTION
        default:
          return null;
      }
    }

    private void MapOrganizer1To2 (AppointmentItem source, IEvent target, IEntityMappingLogger logger)
    {
      if (source.MeetingStatus != OlMeetingStatus.olNonMeeting)
      {
        using (var organizerWrapper = GenericComObjectWrapper.Create (OutlookUtility.GetEventOrganizerOrNull (source, logger, s_logger, _outlookMajorVersion)))
        {
          if (organizerWrapper.Inner != null)
          {
            if (StringComparer.InvariantCultureIgnoreCase.Compare (organizerWrapper.Inner.Name, source.Organizer) == 0)
            {
              SetOrganizer (target, organizerWrapper.Inner, organizerWrapper.Inner.Address, logger);
            }
            else
            {
              string organizerEmail = OutlookUtility.GetSenderEmailAddressOrNull (source, logger, s_logger);
              SetOrganizer (target, source.Organizer, organizerEmail, logger);
            }
          }
        }
      }
    }
   
    private void SetOrganizer (IEvent target, AddressEntry organizer, string address, IEntityMappingLogger logger)
    {
      string organizerEmail = GetMailUrlOrNull (organizer, address, logger);
      var targetOrganizer = (organizerEmail != null) ? new Organizer (organizerEmail) : new Organizer();
      if (organizer != null)
        targetOrganizer.CommonName = organizer.Name;
      target.Organizer = targetOrganizer;
      if (_configuration.ScheduleAgentClient)
        target.Organizer.Parameters.Add ("SCHEDULE-AGENT", "CLIENT");
      if (_configuration.SendNoAppointmentNotifications)
        target.Properties.Add (new CalendarProperty ("X-SOGO-SEND-APPOINTMENT-NOTIFICATIONS", "NO"));
    }

    private void SetOrganizer (IEvent target, string organizerCN, string organizerEmail, IEntityMappingLogger logger)
    {
      Organizer targetOrganizer;

      if (organizerEmail != null)
      {
        var emailAddress = string.Format ("MAILTO:{0}", organizerEmail);
        if (Uri.IsWellFormedUriString (emailAddress, UriKind.Absolute))
        {
          targetOrganizer = new Organizer (emailAddress);
        }
        else
        {
          s_logger.WarnFormat ("Invalid email address URI {0} for organizer", organizerEmail);
          logger.LogMappingWarning ($"Invalid email address Uri '{organizerEmail}' for organizer");
          targetOrganizer = new Organizer();
        }
      }
      else
      {
        targetOrganizer = new Organizer();
      }

      targetOrganizer.CommonName = organizerCN;
      target.Organizer = targetOrganizer;
      if (_configuration.ScheduleAgentClient)
        target.Organizer.Parameters.Add ("SCHEDULE-AGENT", "CLIENT");
      if (_configuration.SendNoAppointmentNotifications)
        target.Properties.Add (new CalendarProperty ("X-SOGO-SEND-APPOINTMENT-NOTIFICATIONS", "NO"));
    }

    private string GetMailUrlOrNull (AddressEntry addressEntry, string defaultMailAddress, IEntityMappingLogger logger)
    {
      return CreateMailUriOrNull(OutlookUtility.GetEmailAdressOrNull(addressEntry, logger,s_logger) ?? defaultMailAddress, logger);
    }
    

    private static string CreateMailUriOrNull (string emailAddressOrNull, IEntityMappingLogger logger)
    {
      if (!string.IsNullOrEmpty (emailAddressOrNull))
      {
        var emailAddressUriString = string.Format ("MAILTO:{0}", emailAddressOrNull);
        if (!Uri.IsWellFormedUriString (emailAddressUriString, UriKind.Absolute))
        {
          s_logger.WarnFormat ("Invalid email address URI {0} for attendee.", emailAddressUriString);
          logger.LogMappingWarning ($"Invalid email address Uri '{emailAddressUriString}' for attendee.");
          return null;
        }
        return emailAddressUriString;
      }
      else
      {
        return null;
      }
    }

    private void MapRecurrance1To2 (AppointmentItem source, IEvent target, ITimeZone startIcalTimeZone, ITimeZone endIcalTimeZone, IEntityMappingLogger logger)
    {
      if (source.IsRecurring)
      {
        using (var sourceRecurrencePatternWrapper = GenericComObjectWrapper.Create (source.GetRecurrencePattern()))
        {
          var sourceRecurrencePattern = sourceRecurrencePatternWrapper.Inner;
          IRecurrencePattern targetRecurrencePattern = new RecurrencePattern();
          if (!sourceRecurrencePattern.NoEndDate)
          {
            targetRecurrencePattern.Count = sourceRecurrencePattern.Occurrences;
            //Until must not be set if count is set, since outlook always sets Occurrences
            //but sogo wants it as utc end time of the last event not only the enddate at 0000
            //targetRecurrencePattern.Until = sourceRecurrencePattern.PatternEndDate.Add(sourceRecurrencePattern.EndTime.TimeOfDay).ToUniversalTime();
          }
          if (sourceRecurrencePattern.Interval >= 1)
          {
            targetRecurrencePattern.Interval = (sourceRecurrencePattern.RecurrenceType == OlRecurrenceType.olRecursYearly ||
                                                sourceRecurrencePattern.RecurrenceType == OlRecurrenceType.olRecursYearNth) ? sourceRecurrencePattern.Interval / 12 : sourceRecurrencePattern.Interval;
          }
          switch (sourceRecurrencePattern.RecurrenceType)
          {
            case OlRecurrenceType.olRecursDaily:
              targetRecurrencePattern.Frequency = FrequencyType.Daily;
              break;
            case OlRecurrenceType.olRecursWeekly:
              targetRecurrencePattern.Frequency = FrequencyType.Weekly;
              CommonEntityMapper.MapDayOfWeek1To2 (sourceRecurrencePattern.DayOfWeekMask, targetRecurrencePattern.ByDay);
              break;
            case OlRecurrenceType.olRecursMonthly:
              targetRecurrencePattern.Frequency = FrequencyType.Monthly;
              targetRecurrencePattern.ByMonthDay.Add (sourceRecurrencePattern.DayOfMonth);
              break;
            case OlRecurrenceType.olRecursMonthNth:
              targetRecurrencePattern.Frequency = FrequencyType.Monthly;

              if (sourceRecurrencePattern.Instance == 5)
              {
                targetRecurrencePattern.BySetPosition.Add (-1);
                CommonEntityMapper.MapDayOfWeek1To2 (sourceRecurrencePattern.DayOfWeekMask, targetRecurrencePattern.ByDay);
              }
              else if (sourceRecurrencePattern.Instance > 0)
              {
                targetRecurrencePattern.BySetPosition.Add (sourceRecurrencePattern.Instance);
                CommonEntityMapper.MapDayOfWeek1To2 (sourceRecurrencePattern.DayOfWeekMask, targetRecurrencePattern.ByDay);
              }
              else
              {
                CommonEntityMapper.MapDayOfWeek1To2 (sourceRecurrencePattern.DayOfWeekMask, targetRecurrencePattern.ByDay);
              }
              break;
            case OlRecurrenceType.olRecursYearly:
              targetRecurrencePattern.Frequency = FrequencyType.Yearly;
              targetRecurrencePattern.ByMonthDay.Add (sourceRecurrencePattern.DayOfMonth);
              targetRecurrencePattern.ByMonth.Add (sourceRecurrencePattern.MonthOfYear);
              break;
            case OlRecurrenceType.olRecursYearNth:
              targetRecurrencePattern.Frequency = FrequencyType.Yearly;
              if (sourceRecurrencePattern.Instance == 5)
              {
                targetRecurrencePattern.BySetPosition.Add (-1);
                CommonEntityMapper.MapDayOfWeek1To2 (sourceRecurrencePattern.DayOfWeekMask, targetRecurrencePattern.ByDay);
              }
              else if (sourceRecurrencePattern.Instance > 0)
              {
                targetRecurrencePattern.BySetPosition.Add (sourceRecurrencePattern.Instance);
                CommonEntityMapper.MapDayOfWeek1To2 (sourceRecurrencePattern.DayOfWeekMask, targetRecurrencePattern.ByDay);
              }
              else
              {
                CommonEntityMapper.MapDayOfWeek1To2 (sourceRecurrencePattern.DayOfWeekMask, targetRecurrencePattern.ByDay);
              }
              targetRecurrencePattern.ByMonth.Add (sourceRecurrencePattern.MonthOfYear);
              break;
          }

          target.RecurrenceRules.Add (targetRecurrencePattern);

          Dictionary<DateTime, PeriodList> targetExceptionDatesByDate = new Dictionary<DateTime, PeriodList>();
          HashSet<DateTime> originalOutlookDatesWithExceptions = new HashSet<DateTime>();

          foreach (var sourceException in sourceRecurrencePattern.Exceptions.ToSafeEnumerable<Exception>())
          {
            if (!sourceException.Deleted)
            { 
              originalOutlookDatesWithExceptions.Add (sourceException.OriginalDate.Date.Add (source.StartInStartTimeZone.TimeOfDay));
            }
          }
          foreach (var sourceException in sourceRecurrencePattern.Exceptions.ToSafeEnumerable<Exception>())
          {
            if (!sourceException.Deleted)
            {
              targetExceptionDatesByDate.Remove (sourceException.OriginalDate.Date);

              try
              {
                using (var wrapper = new AppointmentItemWrapper (sourceException.AppointmentItem, _ => { throw new InvalidOperationException ("Cannot reload exception AppointmentITem!"); }))
                {
                  var targetException = new Event();
                  target.Calendar.Events.Add (targetException);
                  targetException.UID = target.UID;
                  Map1To2 (wrapper.Inner, targetException, true, startIcalTimeZone, endIcalTimeZone, logger);

                  // check if new exception is already present in target
                  // if it is found and not already present as exdate then add a new exdate to avoid 2 events
                  var from = (wrapper.Inner.Start.Date < sourceException.OriginalDate.Date) ? wrapper.Inner.Start.Date : sourceException.OriginalDate.Date;
                  var to = (wrapper.Inner.Start.Date > sourceException.OriginalDate.Date) ? wrapper.Inner.Start.Date.AddDays (1) : sourceException.OriginalDate.Date.AddDays (1);

                  var targetContainsExceptionList = target.GetOccurrences (from, to);
                  foreach (var el in targetContainsExceptionList)
                  {
                    if (!originalOutlookDatesWithExceptions.Contains (el.Period.StartTime.Value))
                    {
                      PeriodList targetExList = new PeriodList();

                      if (!el.Period.StartTime.HasTime)
                      {
                        iCalDateTime exDate = new iCalDateTime (el.Period.StartTime.Date);
                        exDate.HasTime = false;
                        targetExList.Add (exDate);
                        targetExList.Parameters.Add ("VALUE", "DATE");
                      }
                      else
                      {
                        targetExList.Add (new iCalDateTime (el.Period.StartTime.AsUtc()) { IsUniversalTime = true });
                      }
                      if (!targetExceptionDatesByDate.ContainsKey (el.Period.StartTime.Date))
                        targetExceptionDatesByDate.Add (el.Period.StartTime.Date, targetExList);
                    }
                  }

                  if (source.AllDayEvent)
                  {
                    // Outlook's AllDayEvent relates to Start and not not StartUtc!!!
                    targetException.RecurrenceID = new iCalDateTime (sourceException.OriginalDate);
                    targetException.RecurrenceID.HasTime = false;
                  }
                  else
                  {
                    DateTimeZone tz = DateTimeZoneProviders.Bcl.GetSystemDefault();
                    LocalDateTime localExDateTime = LocalDateTime.FromDateTime (sourceException.OriginalDate);
                    ZonedDateTime zonedExDateTime = tz.AtLeniently (localExDateTime);
                    var originalDateUtc = zonedExDateTime.ToDateTimeUtc(); 
                    targetException.RecurrenceID = new iCalDateTime (originalDateUtc) { IsUniversalTime = true };
                  }
                }
              }
              catch (COMException ex)
              {
                s_logger.Warn ("Can't get AppointmentItem of Exception, ignoring!", ex);
                logger.LogMappingWarning ("Can't get AppointmentItem of Exception, ignoring!", ex);
              }
              catch (ArgumentException x)
              {
                s_logger.Warn ("Can't get AppointmentItem of Exception, ignoring!", x);
                logger.LogMappingWarning ("Can't get AppointmentItem of Exception, ignoring!", x);
              }
            }
            else
            {
              if (!originalOutlookDatesWithExceptions.Contains (sourceException.OriginalDate))
              {
                PeriodList targetExList = new PeriodList();

                if (source.AllDayEvent)
                {
                  iCalDateTime exDate = new iCalDateTime (sourceException.OriginalDate);
                  exDate.HasTime = false;
                  targetExList.Add (exDate);
                  targetExList.Parameters.Add ("VALUE", "DATE");
                }
                else
                {
                  string startTimeZoneID;
                  using (var startTimeZone = GenericComObjectWrapper.Create (source.StartTimeZone))
                  {
                    startTimeZoneID = startTimeZone.Inner.ID;
                  }
                  var timeZone = TimeZoneInfo.FindSystemTimeZoneById (startTimeZoneID);
                  var originalDateUtc = TimeZoneInfo.ConvertTimeToUtc (sourceException.OriginalDate, timeZone);
                  iCalDateTime exDate = new iCalDateTime (originalDateUtc.Add (source.StartInStartTimeZone.TimeOfDay)) { IsUniversalTime = true };

                  targetExList.Add (exDate);
                }
                if (!targetExceptionDatesByDate.ContainsKey (sourceException.OriginalDate))
                  targetExceptionDatesByDate.Add (sourceException.OriginalDate, targetExList);
              }
            }
          }
          target.ExceptionDates.AddRange (targetExceptionDatesByDate.Values);
        }
      }
    }

    private void MapRecurrance2To1 (IEvent source, IReadOnlyCollection<IEvent> exceptions, AppointmentItemWrapper targetWrapper, IEntityMappingLogger logger)
    {
      if (source.RecurrenceRules.Count > 0)
      {
        using (var targetRecurrencePatternWrapper = GenericComObjectWrapper.Create (targetWrapper.Inner.GetRecurrencePattern()))
        {
          var targetRecurrencePattern = targetRecurrencePatternWrapper.Inner;
          if (source.RecurrenceRules.Count > 1)
          {
            s_logger.WarnFormat ("Event '{0}' contains more than one recurrence rule. Since outlook supports only one rule, all except the first one will be ignored.", source.UID);
            logger.LogMappingWarning ("Event contains more than one recurrence rule. Since outlook supports only one rule, all except the first one will be ignored.");
          }
          var sourceRecurrencePattern = source.RecurrenceRules[0];

          switch (sourceRecurrencePattern.Frequency)
          {
            case FrequencyType.Daily:
              targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursDaily;
              break;
            case FrequencyType.Weekly:
              if (sourceRecurrencePattern.ByDay.Count > 0)
              {
                targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursWeekly;
                targetRecurrencePattern.DayOfWeekMask = CommonEntityMapper.MapDayOfWeek2To1 (sourceRecurrencePattern.ByDay);
              }
              else
              {
                targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursWeekly;
              }
              break;
            case FrequencyType.Monthly:
              if (sourceRecurrencePattern.ByDay.Count > 0)
              {
                targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursMonthNth;
                if (sourceRecurrencePattern.ByWeekNo.Count > 1)
                {
                  s_logger.WarnFormat ("Event '{0}' contains more than one week in a monthly recurrence rule. Since outlook supports only one week, all except the first one will be ignored.", source.UID);
                  logger.LogMappingWarning ("Event contains more than one week in a monthly recurrence rule. Since outlook supports only one week, all except the first one will be ignored.");
                }
                else if (sourceRecurrencePattern.ByWeekNo.Count > 0)
                {
                  targetRecurrencePattern.Instance = sourceRecurrencePattern.ByWeekNo[0];
                }
                else
                {
                  targetRecurrencePattern.Instance = (sourceRecurrencePattern.ByDay[0].Offset >= 0) ? sourceRecurrencePattern.ByDay[0].Offset : 5;
                }
                if (sourceRecurrencePattern.BySetPosition.Count > 0)
                {
                  targetRecurrencePattern.Instance = (sourceRecurrencePattern.BySetPosition[0] >= 0) ? sourceRecurrencePattern.BySetPosition[0] : 5;
                }
                targetRecurrencePattern.DayOfWeekMask = CommonEntityMapper.MapDayOfWeek2To1 (sourceRecurrencePattern.ByDay);
              }
              else if (sourceRecurrencePattern.ByMonthDay.Count > 0)
              {
                targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursMonthly;
                if (sourceRecurrencePattern.ByMonthDay.Count > 1)
                {
                  s_logger.WarnFormat ("Event '{0}' contains more than one days in a monthly recurrence rule. Since outlook supports only one day, all except the first one will be ignored.", source.UID);
                  logger.LogMappingWarning ("Event contains more than one days in a monthly recurrence rule. Since outlook supports only one day, all except the first one will be ignored.");
                }
                try
                {
                  targetRecurrencePattern.DayOfMonth = sourceRecurrencePattern.ByMonthDay[0];
                }
                catch (COMException ex)
                {
                  s_logger.Warn ($"Recurring event '{source.UID}' contains invalid BYMONTHDAY '{sourceRecurrencePattern.ByMonthDay[0]}', which will be ignored.", ex);
                  logger.LogMappingWarning ($"Recurring event '{source.UID}' contains invalid BYMONTHDAY '{sourceRecurrencePattern.ByMonthDay[0]}', which will be ignored.", ex);
                }
              }
              else
              {
                targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursMonthly;
              }
              break;
            case FrequencyType.Yearly:
              if (sourceRecurrencePattern.ByMonth.Count > 0 && sourceRecurrencePattern.ByWeekNo.Count > 0)
              {
                targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursYearNth;
                if (sourceRecurrencePattern.ByMonth.Count > 1)
                {
                  s_logger.WarnFormat ("Event '{0}' contains more than one months in a yearly recurrence rule. Since outlook supports only one month, all except the first one will be ignored.", source.UID);
                  logger.LogMappingWarning ("Event contains more than one months in a yearly recurrence rule. Since outlook supports only one month, all except the first one will be ignored.");
                }
                if (sourceRecurrencePattern.ByMonth[0] < 1 || sourceRecurrencePattern.ByMonth[0] > 12)
                {
                  s_logger.Warn ($"Recurring event '{source.UID}' contains invalid BYMONTH '{sourceRecurrencePattern.ByMonth[0]}', which will be ignored.");
                  logger.LogMappingWarning ($"Recurring event '{source.UID}' contains invalid BYMONTH '{sourceRecurrencePattern.ByMonth[0]}', which will be ignored.");
                }
                else
                  targetRecurrencePattern.MonthOfYear = sourceRecurrencePattern.ByMonth[0];

                if (sourceRecurrencePattern.ByWeekNo.Count > 1)
                {
                  s_logger.WarnFormat ("Event '{0}' contains more than one week in a yearly recurrence rule. Since outlook supports only one week, all except the first one will be ignored.", source.UID);
                  logger.LogMappingWarning ("Event contains more than one week in a yearly recurrence rule. Since outlook supports only one week, all except the first one will be ignored.");
                }
                targetRecurrencePattern.Instance = sourceRecurrencePattern.ByWeekNo[0];

                targetRecurrencePattern.DayOfWeekMask = CommonEntityMapper.MapDayOfWeek2To1 (sourceRecurrencePattern.ByDay);
              }
              else if (sourceRecurrencePattern.ByMonth.Count > 0 && sourceRecurrencePattern.ByMonthDay.Count > 0)
              {
                targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursYearly;
                if (sourceRecurrencePattern.ByMonth.Count > 1)
                {
                  s_logger.WarnFormat ("Event '{0}' contains more than one months in a yearly recurrence rule. Since outlook supports only one month, all except the first one will be ignored.", source.UID);
                  logger.LogMappingWarning ("Event contains more than one months in a yearly recurrence rule. Since outlook supports only one month, all except the first one will be ignored.");
                }
                if (sourceRecurrencePattern.ByMonth[0] != targetRecurrencePattern.MonthOfYear)
                {
                  if (sourceRecurrencePattern.ByMonth[0] < 1 || sourceRecurrencePattern.ByMonth[0] > 12)
                  {
                    s_logger.Warn ($"Recurring event '{source.UID}' contains invalid BYMONTH '{sourceRecurrencePattern.ByMonth[0]}', which will be ignored.");
                    logger.LogMappingWarning ($"Recurring event '{source.UID}' contains invalid BYMONTH '{sourceRecurrencePattern.ByMonth[0]}', which will be ignored.");
                  }
                  else
                    targetRecurrencePattern.MonthOfYear = sourceRecurrencePattern.ByMonth[0];
                }

                if (sourceRecurrencePattern.ByMonthDay.Count > 1)
                {
                  s_logger.WarnFormat ("Event '{0}' contains more than one days in a monthly recurrence rule. Since outlook supports only one day, all except the first one will be ignored.", source.UID);
                  logger.LogMappingWarning ("Event contains more than one days in a monthly recurrence rule. Since outlook supports only one day, all except the first one will be ignored.");
                }
                if (sourceRecurrencePattern.ByMonthDay[0] != targetRecurrencePattern.DayOfMonth)
                {
                  try
                  {
                    targetRecurrencePattern.DayOfMonth = sourceRecurrencePattern.ByMonthDay[0];
                  }
                  catch (COMException ex)
                  {
                    s_logger.Warn ($"Recurring event '{source.UID}' contains invalid BYMONTHDAY '{sourceRecurrencePattern.ByMonthDay[0]}', which will be ignored.", ex);
                    logger.LogMappingWarning ($"Recurring event '{source.UID}' contains invalid BYMONTHDAY '{sourceRecurrencePattern.ByMonthDay[0]}', which will be ignored.", ex);
                  }
                }
              }
              else if (sourceRecurrencePattern.ByMonth.Count > 0 && sourceRecurrencePattern.ByDay.Count > 0)
              {
                targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursYearNth;
                if (sourceRecurrencePattern.ByMonth.Count > 1)
                {
                  s_logger.WarnFormat ("Event '{0}' contains more than one months in a yearly recurrence rule. Since outlook supports only one month, all except the first one will be ignored.", source.UID);
                  logger.LogMappingWarning ("Event contains more than one months in a yearly recurrence rule. Since outlook supports only one month, all except the first one will be ignored.");
                }
                if (sourceRecurrencePattern.ByMonth[0] < 1 || sourceRecurrencePattern.ByMonth[0] > 12)
                {
                  s_logger.Warn ($"Recurring event '{source.UID}' contains invalid BYMONTH '{sourceRecurrencePattern.ByMonth[0]}', which will be ignored.");
                  logger.LogMappingWarning ($"Recurring event '{source.UID}' contains invalid BYMONTH '{sourceRecurrencePattern.ByMonth[0]}', which will be ignored.");
                }
                else
                  targetRecurrencePattern.MonthOfYear = sourceRecurrencePattern.ByMonth[0];

                targetRecurrencePattern.Instance = (sourceRecurrencePattern.ByDay[0].Offset >= 0) ? sourceRecurrencePattern.ByDay[0].Offset : 5;
                if (sourceRecurrencePattern.BySetPosition.Count > 0)
                {
                  targetRecurrencePattern.Instance = (sourceRecurrencePattern.BySetPosition[0] >= 0) ? sourceRecurrencePattern.BySetPosition[0] : 5;
                }
                targetRecurrencePattern.DayOfWeekMask = CommonEntityMapper.MapDayOfWeek2To1 (sourceRecurrencePattern.ByDay);
              }
              else
              {
                targetRecurrencePattern.RecurrenceType = OlRecurrenceType.olRecursYearly;
              }
              break;
            default:
              s_logger.WarnFormat ("Recurring event '{0}' contains the Frequency '{1}', which is not supported by outlook. Ignoring recurrence rule.", source.UID, sourceRecurrencePattern.Frequency);
              logger.LogMappingWarning ($"Recurring event contains the Frequency '{sourceRecurrencePattern.Frequency}', which is not supported by outlook. Ignoring recurrence rule.");
              targetWrapper.Inner.ClearRecurrencePattern();
              break;
          }

          try
          {
            targetRecurrencePattern.Interval = (targetRecurrencePattern.RecurrenceType == OlRecurrenceType.olRecursYearly ||
                                                targetRecurrencePattern.RecurrenceType == OlRecurrenceType.olRecursYearNth) ? sourceRecurrencePattern.Interval * 12 : sourceRecurrencePattern.Interval;
          }
          catch (COMException ex)
          {
            s_logger.Warn ($"Recurring event '{source.UID}' contains the Interval '{sourceRecurrencePattern.Interval}', which is not supported by outlook. Ignoring interval.", ex);
            logger.LogMappingWarning ($"Recurring event contains the Interval '{sourceRecurrencePattern.Interval}', which is not supported by outlook. Ignoring interval.", ex);
          }

          if (sourceRecurrencePattern.Count >= 0)
            targetRecurrencePattern.Occurrences = sourceRecurrencePattern.Count;

          if (sourceRecurrencePattern.Until != default(DateTime))
          {
            if (sourceRecurrencePattern.Until.Date >= targetRecurrencePattern.PatternStartDate)
              targetRecurrencePattern.PatternEndDate = sourceRecurrencePattern.Until.Date;
            else
              targetRecurrencePattern.PatternEndDate = targetRecurrencePattern.PatternStartDate;
          }
        }
        // Due to limitations out outlook, the Appointment has to be saved here. Otherwise 'targetRecurrencePattern.GetOccurrence ()'
        // will throw an exception

        targetWrapper.SaveAndReload();

        using (var targetRecurrencePatternWrapper = GenericComObjectWrapper.Create (targetWrapper.Inner.GetRecurrencePattern()))
        {
          var targetRecurrencePattern = targetRecurrencePatternWrapper.Inner;

          if (source.ExceptionDates != null)
          {
            foreach (IPeriodList exdateList in source.ExceptionDates)
            {
              foreach (IPeriod exdate in exdateList)
              {
                try
                {
                  string startTimeZoneID;
                  using (var startTimeZone = GenericComObjectWrapper.Create (targetWrapper.Inner.StartTimeZone))
                  {
                    startTimeZoneID = startTimeZone.Inner.ID;
                  }

                  NodaTime.DateTimeZone startZone = NodaTime.DateTimeZoneProviders.Bcl[startTimeZoneID];
                  DateTime originalStart;

                  if (exdate.StartTime.IsUniversalTime)
                  {
                    originalStart = NodaTime.Instant.FromDateTimeUtc (exdate.StartTime.Value).InZone (startZone).ToDateTimeUnspecified().Date;
                  }
                  else
                  {
                    originalStart = exdate.StartTime.Date;
                  }
                  var originalExDate = NodaTime.LocalDateTime.FromDateTime (originalStart.Add (targetWrapper.Inner.StartInStartTimeZone.TimeOfDay));

                  NodaTime.ZonedDateTime zonedExDate = originalExDate.InZoneLeniently (startZone);
                  NodaTime.ZonedDateTime localExDate = zonedExDate.WithZone (NodaTime.DateTimeZoneProviders.Bcl.GetSystemDefault());

                  using (var wrapper = GenericComObjectWrapper.Create (targetRecurrencePattern.GetOccurrence (localExDate.ToDateTimeUnspecified())))
                  {
                    wrapper.Inner.Delete();
                  }
                }
                catch (COMException ex)
                {
                  s_logger.Warn ("Can't find occurence of exception, ignoring.", ex);
                  logger.LogMappingWarning ("Can't find occurence of exception, ignoring.", ex);
                }
              }
            }
          }
          // to prevent skipping of occurences while moving (outlook throws exception when skipping occurences), moving has to be done in two steps
          // first move all exceptions which are preponed from earliest to latest
          MapRecurrenceExceptions2To1 (
              exceptions.Where (e => e.Start.AsUtc() < e.RecurrenceID.Date).OrderBy (e => e.Start.AsUtc()),
              targetWrapper,
              targetRecurrencePattern,
              logger);
          // then move all exceptions which are postponed or are not moved from last to first
          MapRecurrenceExceptions2To1 (
              exceptions.Where (e => e.Start.AsUtc() >= e.RecurrenceID.Date).OrderByDescending (e => e.Start.AsUtc()),
              targetWrapper,
              targetRecurrencePattern,
              logger);
          // HINT: this algorith will only prevent skipping while moving. If the final state contains skipped occurences, outlook will throw an exception anyway
        }
      }
    }

    private void MapRecurrenceExceptions2To1 (
        IEnumerable<IEvent> exceptions,
        AppointmentItemWrapper targetWrapper,
        Microsoft.Office.Interop.Outlook.RecurrencePattern targetRecurrencePattern,
        IEntityMappingLogger logger)
    {
      foreach (var recurranceException in exceptions)
      {
        try
        {
          string startTimeZoneID;
          using (var startTimeZone = GenericComObjectWrapper.Create (targetWrapper.Inner.StartTimeZone))
          {
            startTimeZoneID = startTimeZone.Inner.ID;
          }

          NodaTime.DateTimeZone startZone = NodaTime.DateTimeZoneProviders.Bcl[startTimeZoneID];
          DateTime originalStart;

          if (recurranceException.RecurrenceID.IsUniversalTime)
          {
            originalStart = NodaTime.Instant.FromDateTimeUtc (recurranceException.RecurrenceID.Value).InZone (startZone).ToDateTimeUnspecified().Date;
          }
          else
          {
            originalStart = recurranceException.RecurrenceID.Date;
          }

          var originalExDate = NodaTime.LocalDateTime.FromDateTime (originalStart.Add (targetWrapper.Inner.StartInStartTimeZone.TimeOfDay));
          NodaTime.ZonedDateTime zonedExDate = originalExDate.InZoneLeniently (startZone);
          NodaTime.ZonedDateTime localExDate = zonedExDate.WithZone (NodaTime.DateTimeZoneProviders.Bcl.GetSystemDefault());

          var targetException = targetRecurrencePattern.GetOccurrence (localExDate.ToDateTimeUnspecified());

          using (var exceptionWrapper = new AppointmentItemWrapper (targetException, _ => { throw new InvalidOperationException ("cannot reload exception item"); }))

          {
            Map2To1 (recurranceException, new IEvent[] { }, exceptionWrapper, true, logger);
            exceptionWrapper.Inner.Save();
          }
        }
        catch (COMException ex)
        {
          s_logger.Warn ("Can't find occurence of exception or exception can't be saved, ignoring.", ex);
          logger.LogMappingWarning ("Can't find occurence of exception or exception can't be saved, ignoring.", ex);
        }
      }
    }

    private void MapAttendees1To2 (AppointmentItem source, IEvent target, out bool organizerSet, IEntityMappingLogger logger)
    {
      organizerSet = false;
      bool ownAttendeeSet = false;

      foreach (var recipient in source.Recipients.ToSafeEnumerable<Recipient>())
      {
        string recipientMailAddressOrNull = null;
        try
        {
          if (recipient.Resolve())
          {
            using (var entryWrapper = GenericComObjectWrapper.Create (recipient.AddressEntry))
            {
              recipientMailAddressOrNull = OutlookUtility.GetEmailAdressOrNull (entryWrapper.Inner, logger, s_logger);
            }
          }
        }
        catch (COMException ex)
        {
          s_logger.Warn ("Can't get AddressEntry of recipient", ex);
          logger.LogMappingWarning ("Can't get AddressEntry of recipient", ex);
        }

        if (!IsOwnIdentity (recipientMailAddressOrNull))
        {
          Attendee attendee;

          if (!string.IsNullOrEmpty (recipient.Address))
          {
            var recipientMailUrl = CreateMailUriOrNull (recipientMailAddressOrNull ?? recipient.Address, logger);
            if (recipientMailUrl != null)
            {
              attendee = new Attendee (recipientMailUrl);
            }
            else
            {
              attendee = new Attendee();
            }
          }
          else
          {
            attendee = new Attendee();
          }

          attendee.ParticipationStatus = MapParticipation1To2 (recipient.MeetingResponseStatus);
          attendee.CommonName = recipient.Name;
          attendee.Role = MapAttendeeType1To2 ((OlMeetingRecipientType) recipient.Type);
          if ((OlMeetingRecipientType) recipient.Type == OlMeetingRecipientType.olResource)
            attendee.Type = "RESOURCE";
          attendee.RSVP = true;
          if (_configuration.ScheduleAgentClient)
            attendee.Parameters.Add ("SCHEDULE-AGENT", "CLIENT");
          target.Attendees.Add (attendee);
        }
        else
        {
          if ((source.MeetingStatus == OlMeetingStatus.olMeetingReceived || source.MeetingStatus == OlMeetingStatus.olMeetingReceivedAndCanceled) && (!ownAttendeeSet))
          {
            Attendee ownAttendee;

            if (!string.IsNullOrEmpty (recipient.Address))
            {
              var recipientMailUrl = CreateMailUriOrNull (recipientMailAddressOrNull ?? recipient.Address, logger);
              if (recipientMailUrl != null)
              {
                ownAttendee = new Attendee (recipientMailUrl);
              }
              else
              {
                ownAttendee = new Attendee();
              }
            }
            else
            {
              ownAttendee = new Attendee();
            }
            ownAttendee.CommonName = recipient.Name;
            ownAttendee.ParticipationStatus = (source.MeetingStatus == OlMeetingStatus.olMeetingReceivedAndCanceled) ? "DECLINED" : MapParticipation1To2 (source.ResponseStatus);
            ownAttendee.Role = MapAttendeeType1To2 ((OlMeetingRecipientType) recipient.Type);
            if (_configuration.ScheduleAgentClient)
              ownAttendee.Parameters.Add ("SCHEDULE-AGENT", "CLIENT");
            target.Attendees.Add (ownAttendee);
            ownAttendeeSet = true;
          }
        }
        if (((OlMeetingRecipientType) recipient.Type) == OlMeetingRecipientType.olOrganizer)
        {
          if (!string.IsNullOrEmpty (recipient.Address))
          {
            using (var entryWrapper = GenericComObjectWrapper.Create (recipient.AddressEntry))
            {
              SetOrganizer (target, entryWrapper.Inner, recipient.Address, logger);
            }
          }
          else
          {
            SetOrganizer (target, recipient.Name, null, logger);
          }

          organizerSet = true;
        }
      }
    }

    private bool IsOwnIdentity (Recipient recipient, IEntityMappingLogger logger)
    {
      try
      {
        if (recipient.Resolve())
        {
          string mailAddress;
          using (var wrapper = GenericComObjectWrapper.Create (recipient.AddressEntry))
            mailAddress = OutlookUtility.GetEmailAdressOrNull (wrapper.Inner, NullEntitySynchronizationLogger.Instance,
              s_logger);
          return IsOwnIdentity (mailAddress);
        }
        else
          return false;
      }
      catch (COMException ex)
      {
        s_logger.Warn ("Can't get AddressEntry of recipient", ex);
        logger.LogMappingWarning ("Can't get AddressEntry of recipient", ex);
        return false;
      }
    }

    private bool IsOwnIdentity (string mailAddress)
    {
      return StringComparer.InvariantCultureIgnoreCase.Compare (mailAddress, _outlookEmailAddress) == 0;
    }

    public string MapAttendeeType1To2 (OlMeetingRecipientType recipientType)
    {
      switch (recipientType)
      {
        case OlMeetingRecipientType.olOptional:
          return "OPT-PARTICIPANT";
        case OlMeetingRecipientType.olRequired:
        case OlMeetingRecipientType.olResource:
          return "REQ-PARTICIPANT";
        case OlMeetingRecipientType.olOrganizer:
          return "CHAIR";
      }

      throw new NotImplementedException (string.Format ("Mapping for value '{0}' not implemented.", recipientType));
    }

    public OlMeetingRecipientType MapAttendeeType2To1 (string recipientType)
    {
      switch (recipientType)
      {
        case null:
        case "NON-PARTICIPANT":
        case "OPT-PARTICIPANT":
          return OlMeetingRecipientType.olOptional;
        case "REQ-PARTICIPANT":
          return OlMeetingRecipientType.olRequired;
        case "CHAIR":
          return OlMeetingRecipientType.olOrganizer;
        case "X-LOCATION":
          return OlMeetingRecipientType.olResource;
        // according to the RFC 5545 unknown values must be treated as REQ-PARTICIPANT
        default:
          return OlMeetingRecipientType.olRequired;
      }
    }


    private const int s_mailtoSchemaLength = 7; // length of "mailto:"

    public Task<AppointmentItemWrapper> Map2To1 (IICalendar sourceCalendar, AppointmentItemWrapper target, IEntityMappingLogger logger)
    {
      IEvent sourceMasterEvent = null;
      IReadOnlyCollection<IEvent> sourceExceptionEvents;

      var sourceEvents = sourceCalendar.Events;

      if (sourceEvents.Count == 1)
      {
        sourceMasterEvent = sourceEvents[0];
        sourceExceptionEvents = new IEvent[] { };
      }
      else
      {
        var sourceExceptionEventsList = new List<IEvent>();
        sourceExceptionEvents = sourceExceptionEventsList;

        foreach (var sourceEvent in sourceEvents)
        {
          if (sourceEvent.RecurrenceID == null)
            sourceMasterEvent = sourceEvent;
          else
            sourceExceptionEventsList.Add (sourceEvent);
        }

        // TODO
        // Maybe it is a good idea to sort the exception events here by RecurrenceId
      }

      if (sourceMasterEvent == null)
      {
        s_logger.Warn ("Detected CalDav Event with contains only exceptions. Reconstructing master event.");
        logger.LogMappingWarning ("CalDav Ressources contains only exceptions. Reconstructing master event.");
        AddMasterEvent (sourceCalendar);
        return Map2To1 (sourceCalendar, target, logger);
      }

      return Task.FromResult(Map2To1 (sourceMasterEvent, sourceExceptionEvents, target, false, logger));
    }

    private void AddMasterEvent (IICalendar calendar)
    {
      if (calendar.Events.Count < 2)
        throw new ArgumentException ("Calendar has to contain at least two events", nameof (calendar));

      var sortedEvents = calendar.Events.OrderBy (e => e.RecurrenceID).ToArray();

      var masterEvent = new Event();
      var firstException = sortedEvents[0];
      masterEvent.Start = firstException.RecurrenceID;
      masterEvent.Summary = firstException.Summary;
      masterEvent.Location = firstException.Location;
      masterEvent.Class = firstException.Class;
      masterEvent.Categories = firstException.Categories;
      masterEvent.Organizer = firstException.Organizer;

      var sortedExceptionsWithDistance =
          new[] { new { Event = firstException, DistanceFromMasterInDays = 0 } }
              .Union (
                  sortedEvents
                      .Zip (
                          sortedEvents.Skip (1),
                          (first, second) => new
                                             {
                                                 Event = second,
                                                 DistanceFromMasterInDays = (int) Math.Round ((second.RecurrenceID.Value - first.RecurrenceID.Value).TotalDays, MidpointRounding.AwayFromZero)
                                             }))
              .ToArray();

      var intervalInDays = GreatestCommonDivisor (sortedExceptionsWithDistance.Select (d => d.DistanceFromMasterInDays));

      var numberOfEceptions = sortedExceptionsWithDistance.Last().DistanceFromMasterInDays / intervalInDays +1;
      masterEvent.RecurrenceRules.Add (new RecurrencePattern (FrequencyType.Daily, intervalInDays)
                                       {
                                           Count = numberOfEceptions
                                       });

      var exDates = new PeriodList();

      int currentExceptionIndex = 0;
      for (int occurence = 0; occurence < numberOfEceptions; occurence++)
      {
        var currentDistanceFromMasterInDays = occurence * intervalInDays;
        var originalDate = masterEvent.Start.AddDays (currentDistanceFromMasterInDays);
        if (sortedExceptionsWithDistance[currentExceptionIndex].DistanceFromMasterInDays == currentDistanceFromMasterInDays)
        {
          // The recurrence Id has to be set, since the original value was rounded to calculate the interval
          sortedExceptionsWithDistance[currentExceptionIndex].Event.RecurrenceID = originalDate;
          currentExceptionIndex++;
        }
        else
        {
          exDates.Add (new Period (originalDate));
        }
      }

      masterEvent.ExceptionDates.Add (exDates);
      calendar.Events.Add (masterEvent);
    }

    static int GreatestCommonDivisor (int a, int b)
    {
      return b == 0 ? a : GreatestCommonDivisor (b, a % b);
    }

    private static int GreatestCommonDivisor (IEnumerable<int> values)
    {
      return values.Aggregate (GreatestCommonDivisor);
    }

    private AppointmentItemWrapper Map2To1 (
        IEvent source,
        IReadOnlyCollection<IEvent> recurrenceExceptionsOrNull,
        AppointmentItemWrapper targetWrapper,
        bool isRecurrenceException,
        IEntityMappingLogger logger)
    {
      if (!isRecurrenceException && targetWrapper.Inner.IsRecurring)
      {
        targetWrapper.Inner.ClearRecurrencePattern();
        targetWrapper.SaveAndReload();
      }

      if (source.IsAllDay)
      {
        targetWrapper.Inner.Start = source.Start.Value;
        if (source.End == null)
        {
          targetWrapper.Inner.End = source.Start.Value.AddDays (1);
        }
        else if (source.End.Value <= source.Start.Value)
        {
          s_logger.Warn ("Invalid EndDate of appointment, setting to StartDate + 1 day.");
          logger.LogMappingWarning ("Invalid EndDate of appointment, setting to StartDate + 1 day.");
          targetWrapper.Inner.End = source.Start.Value.AddDays (1);
        }
        else
        {
          targetWrapper.Inner.End = source.End.Value;
        }
        targetWrapper.Inner.AllDayEvent = true;
      }
      else
      {
        targetWrapper.Inner.AllDayEvent = false;

        if (!string.IsNullOrEmpty (source.Start.TZID))
        {
          try
          {
            var tzi = TimeZoneInfo.FindSystemTimeZoneById (source.Start.TZID);
            targetWrapper.Inner.StartTimeZone = targetWrapper.Inner.Application.TimeZones[source.Start.TZID];
          }
          catch (COMException ex)
          {
            s_logger.Warn ("Can't set StartTimeZone of appointment.", ex);
            logger.LogMappingWarning ("Can't set StartTimeZone of appointment.", ex);
          }
          catch (TimeZoneNotFoundException)
          {
            targetWrapper.Inner.StartTimeZone = targetWrapper.Inner.Application.TimeZones[TimeZoneMapper.IanaToWindows (source.Start.TZID) ?? _localTimeZoneInfo.Id];
          }
        }

        if (source.Start.IsUniversalTime)
        {
          targetWrapper.Inner.StartUTC = source.Start.Value;
        }
        else
        {
          targetWrapper.Inner.StartInStartTimeZone = source.Start.Value;
        }

        if (source.DTEnd != null)
        {
          if (!string.IsNullOrEmpty (source.DTEnd.TZID))
          {
            try
            {
              var tzi = TimeZoneInfo.FindSystemTimeZoneById (source.DTEnd.TZID);
              targetWrapper.Inner.EndTimeZone = targetWrapper.Inner.Application.TimeZones[source.DTEnd.TZID];
            }
            catch (COMException ex)
            {
              s_logger.Warn ("Can't set EndTimeZone of appointment.", ex);
              logger.LogMappingWarning ("Can't set EndTimeZone of appointment.", ex);
            }
            catch (TimeZoneNotFoundException)
            {
              targetWrapper.Inner.EndTimeZone = targetWrapper.Inner.Application.TimeZones[TimeZoneMapper.IanaToWindows (source.DTEnd.TZID) ?? _localTimeZoneInfo.Id];
            }
          }

          try
          {
            if (source.DTEnd.IsUniversalTime)
            {
              targetWrapper.Inner.EndUTC = source.DTEnd.Value;
            }
            else
            {
              targetWrapper.Inner.EndInEndTimeZone = source.DTEnd.Value;
            }
          }
          catch (COMException ex)
          {
            s_logger.Warn ("Invalid EndTime of appointment, setting StartTime.", ex);
            logger.LogMappingWarning ("Invalid EndTime of appointment, setting StartTime.", ex);
            if (source.Start.HasTime)
            {
              targetWrapper.Inner.EndTimeZone = targetWrapper.Inner.StartTimeZone;
              targetWrapper.Inner.End = targetWrapper.Inner.Start;
            }
            else
            {
              targetWrapper.Inner.EndUTC = source.Start.AddDays (1).AsUtc();
            }
          }
        }
        else if (source.Start.HasTime)
        {
          targetWrapper.Inner.EndTimeZone = targetWrapper.Inner.StartTimeZone;
          targetWrapper.Inner.End = targetWrapper.Inner.Start;
        }
        else
        {
          targetWrapper.Inner.EndUTC = source.Start.AddDays (1).AsUtc();
        }
      }

      targetWrapper.Inner.Subject = source.Summary;
      if (source.Status == EventStatus.Cancelled)
      {
        if (string.IsNullOrEmpty (targetWrapper.Inner.Subject))
          targetWrapper.Inner.Subject = "Cancelled: ";
        else if (!targetWrapper.Inner.Subject.StartsWith ("Cancelled: "))
          targetWrapper.Inner.Subject = "Cancelled: " + targetWrapper.Inner.Subject;
      }

      targetWrapper.Inner.Location = source.Location;

      targetWrapper.Inner.Body = _configuration.MapBody ? source.Description : string.Empty;

      targetWrapper.Inner.Importance = CommonEntityMapper.MapPriority2To1 (source.Priority);

      if (_configuration.MapAttendees)
        MapAttendeesAndOrganizer2To1 (source, targetWrapper.Inner, logger);


      if (!isRecurrenceException)
        MapRecurrance2To1 (source, recurrenceExceptionsOrNull, targetWrapper, logger);

      if (!isRecurrenceException)
        targetWrapper.Inner.Sensitivity = CommonEntityMapper.MapPrivacy2To1 (source.Class, 
          _configuration.MapClassConfidentialToSensitivityPrivate, _configuration.MapClassPublicToSensitivityPrivate);

      MapReminder2To1 (source, targetWrapper.Inner, logger);

      if (!isRecurrenceException)
        MapCategories2To1 (source, targetWrapper.Inner);

      targetWrapper.Inner.BusyStatus = MapTransparency2To1 (source);
      
      if (_configuration.MapAttendees && source.Organizer != null)
      {
        var ownSourceAttendee = source.Attendees.FirstOrDefault ((a) =>
        {
          try
          {
            return StringComparer.InvariantCultureIgnoreCase.Compare (a.Value != null ? a.Value.ToString() : null, _serverEmailUri) == 0;
          }
          catch (UriFormatException)
          {
            return false;
          }
        }
            );

        if (source.Status == EventStatus.Cancelled)
        {
          targetWrapper.Inner.MeetingStatus = OlMeetingStatus.olMeetingReceivedAndCanceled;
        }
        else if (ownSourceAttendee != null && targetWrapper.Inner.ResponseStatus != OlResponseStatus.olResponseOrganized)
        {
          var response = MapParticipation2ToMeetingResponse (ownSourceAttendee.ParticipationStatus);
          if ((response != null) && (MapParticipation2To1 (ownSourceAttendee.ParticipationStatus) != targetWrapper.Inner.ResponseStatus))
          {
            if (response == OlMeetingResponse.olMeetingDeclined)
            {
              targetWrapper.Inner.MeetingStatus = OlMeetingStatus.olMeetingReceivedAndCanceled;
            }
            else
            {
              using (var newMeetingItem = GenericComObjectWrapper.Create (targetWrapper.Inner.Respond (response.Value)))
              {
                var newAppointment = newMeetingItem.Inner.GetAssociatedAppointment (false);
                targetWrapper.Replace (newAppointment);
              }
            }
          }
        }
      }

      return targetWrapper;
    }

    private void MapCategories2To1 (IEvent source, AppointmentItem target)
    {
      var categories = string.Join (CultureInfo.CurrentCulture.TextInfo.ListSeparator, source.Categories);

      if (_configuration.UseEventCategoryAsFilter && !_configuration.InvertEventCategoryFilter
          && source.Categories.All (a => a != _configuration.EventCategory))
      {
        target.Categories = categories + CultureInfo.CurrentCulture.TextInfo.ListSeparator + _configuration.EventCategory;
      }
      else
      {
        target.Categories = categories;
      }
    }

    private void MapAttendeesAndOrganizer2To1 (IEvent source, AppointmentItem target, IEntityMappingLogger logger)
    {
      var recipientsToDispose = new HashSet<Recipient>();
      try
      {
        var targetRecipientsWhichShouldRemain = new HashSet<Recipient>();
        var indexByEmailAddresses = GetOutlookRecipientsByEmailAddressesOrName (target, recipientsToDispose, logger);

        foreach (var attendee in source.Attendees)
        {
          Recipient targetRecipient = null;

          string attendeeEmail = string.Empty;
          if (attendee.Value != null)
          {
            try
            {
              attendeeEmail = attendee.Value.ToString();
            }
            catch (UriFormatException ex)
            {
              s_logger.Warn ("Ignoring invalid Uri in attendee email.", ex);
              logger.LogMappingWarning ("Ignoring invalid Uri in attendee email.", ex);
            }
          }
          if (attendeeEmail.Length >= s_mailtoSchemaLength && !string.IsNullOrEmpty (attendeeEmail.Substring (s_mailtoSchemaLength)))
          {
            if (!indexByEmailAddresses.TryGetValue (attendeeEmail, out targetRecipient))
            {
              if (!string.IsNullOrEmpty (attendee.CommonName))
              {
                targetRecipient = target.Recipients.Add (attendee.CommonName + "<" + attendeeEmail.Substring (s_mailtoSchemaLength) + ">");
              }
              else
              {
                targetRecipient = target.Recipients.Add (attendeeEmail.Substring (s_mailtoSchemaLength));
              }
            }
          }
          else
          {
            if (!string.IsNullOrEmpty (attendee.CommonName))
              targetRecipient = target.Recipients.Add (attendee.CommonName);
          }

          if (targetRecipient != null)
          {
            recipientsToDispose.Add (targetRecipient);
            targetRecipientsWhichShouldRemain.Add (targetRecipient);
            targetRecipient.Type = (int) MapAttendeeType2To1 (attendee.Role);
            if (attendee.Type == "RESOURCE" || attendee.Type == "ROOM")
              targetRecipient.Type = (int) OlMeetingRecipientType.olResource;
            targetRecipient.Resolve();
          }
        }

        if (source.Organizer != null && source.Organizer.Value != null)
        {
          string sourceOrganizerEmail = string.Empty;

          try
          {
            sourceOrganizerEmail = source.Organizer.Value.ToString().Substring (s_mailtoSchemaLength);
          }
          catch (UriFormatException ex)
          {
            s_logger.Warn ("Ignoring invalid Uri in organizer email.", ex);
            logger.LogMappingWarning ("Ignoring invalid Uri in organizer email.", ex);
          }

          if (StringComparer.InvariantCultureIgnoreCase.Compare (sourceOrganizerEmail, _outlookEmailAddress) != 0)
          {
            Recipient targetRecipient = null;

            target.MeetingStatus = OlMeetingStatus.olMeetingReceived;

            if (!string.IsNullOrEmpty (sourceOrganizerEmail) && !string.IsNullOrEmpty (source.Organizer.CommonName) && source.Organizer.CommonName != sourceOrganizerEmail) 
            {
              targetRecipient = target.Recipients.Add (source.Organizer.CommonName + "<" + sourceOrganizerEmail + ">");
            }
            else if (!string.IsNullOrEmpty (sourceOrganizerEmail))
            {
              targetRecipient = target.Recipients.Add (sourceOrganizerEmail);
            }
            else if (!string.IsNullOrEmpty (source.Organizer.CommonName))
            {
              targetRecipient = target.Recipients.Add (source.Organizer.CommonName);
            }

            if (targetRecipient != null)
            {
              recipientsToDispose.Add (targetRecipient);
              targetRecipientsWhichShouldRemain.Add (targetRecipient);
              targetRecipient.Type = (int) OlMeetingRecipientType.olOrganizer;

              using (var oPa = GenericComObjectWrapper.Create (target.PropertyAccessor))
              {
                string organizerID = null;

                if (targetRecipient.Resolve())
                {
                  using (var organizerAddressEntry = GenericComObjectWrapper.Create (targetRecipient.AddressEntry))
                  {
                    organizerID = organizerAddressEntry.Inner != null ? organizerAddressEntry.Inner.ID : null;
                  }
                }

                if (organizerID != null && oPa.Inner != null)
                {
                  var propertyTagsSentRepresenting = new object[] { PR_SENT_REPRESENTING_NAME, PR_SENT_REPRESENTING_EMAIL_ADDRESS, PR_SENT_REPRESENTING_ADDRTYPE, PR_SENT_REPRESENTING_ENTRYID };
                  var propertyTagsSender = new object[] { PR_SENDER_NAME, PR_SENDER_EMAIL_ADDRESS, PR_SENT_REPRESENTING_ADDRTYPE, PR_SENDER_ENTRYID };
                  object[] propertyValues;

                  propertyValues = new object[] { targetRecipient.Name, sourceOrganizerEmail, "SMTP", oPa.Inner.StringToBinary (organizerID) };

                  try
                  {
                    oPa.Inner.SetProperties (propertyTagsSentRepresenting, propertyValues);

                    if (_outlookMajorVersion >= 15)
                    {
                      oPa.Inner.SetProperties (propertyTagsSender, propertyValues);
                    }
                  }
                  catch (COMException ex)
                  {
                    s_logger.Warn ("Could not set property PR_SENDER_* for organizer", ex);
                    logger.LogMappingWarning ("Could not set property PR_SENDER_* for organizer", ex);
                  }
                }
              }
            }
          }
          else if (target.Recipients.Count > 0)
          {
            target.MeetingStatus = OlMeetingStatus.olMeeting;
          }
          else
          {
            target.MeetingStatus = OlMeetingStatus.olNonMeeting;
          }
        }
        else
        {
          target.MeetingStatus = OlMeetingStatus.olNonMeeting;
        }

        for (int i = target.Recipients.Count; i > 0; i--)
        {
          var recipient = target.Recipients[i];
          recipientsToDispose.Add (recipient);
          if (!IsOwnIdentity (recipient, logger))
          {
            if (!targetRecipientsWhichShouldRemain.Contains (recipient))
              target.Recipients.Remove (i);
          }
        }
      }
      finally
      {
        recipientsToDispose.ToSafeEnumerable().ToArray();
      }
    }

    private Dictionary<string, Recipient> GetOutlookRecipientsByEmailAddressesOrName (AppointmentItem appointment, HashSet<Recipient> disposeList, IEntityMappingLogger logger)
    {
      Dictionary<string, Recipient> indexByEmailAddresses = new Dictionary<string, Recipient> (StringComparer.InvariantCultureIgnoreCase);

      foreach (Recipient recipient in appointment.Recipients)
      {
        disposeList.Add (recipient);
        if (!string.IsNullOrEmpty (recipient.Address))
        {
          using (var entryWrapper = GenericComObjectWrapper.Create (recipient.AddressEntry))
          {
            indexByEmailAddresses[GetMailUrlOrNull (entryWrapper.Inner, recipient.Address, logger) ?? recipient.Name] = recipient;
          }
        }
        else
        {
          indexByEmailAddresses[recipient.Name] = recipient;
        }
      }

      return indexByEmailAddresses;
    }
  }
}