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
// along with this program.  If not, see <http://www.gnu.org/licenses/>.using System;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CalDavSynchronizer.DataAccess;
using DDay.iCal;
using GenSync.EntityRelationManagement;
using GenSync.EntityRepositories;
using GenSync.Synchronization;

namespace CalDavSynchronizer.Implementation.Events
{
  public class EventSynchronizationContextFactory : ISynchronizationContextFactory<IEventSynchronizationContext>
  {
    private readonly OutlookEventRepository _outlookRepository;
    private readonly IEntityRepository<IICalendar, WebResourceName, string, IEventSynchronizationContext> _btypeRepository;
    private readonly IEntityRelationDataAccess<string, DateTime, WebResourceName, string> _entityRelationDataAccess;
    private readonly bool _cleanupDuplicateEvents;

    public EventSynchronizationContextFactory(
      OutlookEventRepository outlookRepository,
      IEntityRepository<IICalendar, WebResourceName, string, IEventSynchronizationContext> btypeRepository,
      IEntityRelationDataAccess<string, DateTime, WebResourceName, string> entityRelationDataAccess,
      bool cleanupDuplicateEvents)
    {
      if (outlookRepository == null)
        throw new ArgumentNullException (nameof (outlookRepository));
      if (btypeRepository == null)
        throw new ArgumentNullException (nameof (btypeRepository));
      if (entityRelationDataAccess == null)
        throw new ArgumentNullException (nameof (entityRelationDataAccess));

      _outlookRepository = outlookRepository;
      _btypeRepository = btypeRepository;
      _entityRelationDataAccess = entityRelationDataAccess;
      _cleanupDuplicateEvents = cleanupDuplicateEvents;
    }

    public Task<IEventSynchronizationContext> Create ()
    {
      return Task.FromResult(
        _cleanupDuplicateEvents
          ? new DuplicateEventCleaner(
            _outlookRepository,
            _btypeRepository,
            _entityRelationDataAccess)
          : NullEventSynchronizationContext.Instance);
    }

    public async Task SynchronizationFinished (IEventSynchronizationContext context)
    {
      await context.NotifySynchronizationFinished();
    }
  }
}