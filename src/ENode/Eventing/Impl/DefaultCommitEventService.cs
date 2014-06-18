﻿using System;
using ECommon.Logging;
using ECommon.Retring;
using ENode.Commanding;
using ENode.Domain;
using ENode.Infrastructure;

namespace ENode.Eventing.Impl
{
    /// <summary>The default implementation of ICommitService.
    /// </summary>
    public class DefaultCommitEventService : ICommitEventService
    {
        #region Private Variables

        private readonly IExecutedCommandService _executedCommandService;
        private readonly IAggregateRootTypeCodeProvider _aggregateRootTypeCodeProvider;
        private readonly IAggregateRootFactory _aggregateRootFactory;
        private readonly IEventStreamConvertService _eventStreamConvertService;
        private readonly IEventSourcingService _eventSourcingService;
        private readonly IMemoryCache _memoryCache;
        private readonly IAggregateStorage _aggregateStorage;
        private readonly IRetryCommandService _retryCommandService;
        private readonly IEventStore _eventStore;
        private readonly IEventPublisher _eventPublisher;
        private readonly IActionExecutionService _actionExecutionService;
        private readonly IEventSynchronizerProvider _eventSynchronizerProvider;
        private readonly ILogger _logger;

        #endregion

        #region Constructors

        /// <summary>Parameterized constructor.
        /// </summary>
        /// <param name="executedCommandService"></param>
        /// <param name="aggregateRootTypeCodeProvider"></param>
        /// <param name="aggregateRootFactory"></param>
        /// <param name="eventStreamConvertService"></param>
        /// <param name="eventSourcingService"></param>
        /// <param name="memoryCache"></param>
        /// <param name="aggregateStorage"></param>
        /// <param name="retryCommandService"></param>
        /// <param name="eventStore"></param>
        /// <param name="eventPublisher"></param>
        /// <param name="actionExecutionService"></param>
        /// <param name="eventSynchronizerProvider"></param>
        /// <param name="loggerFactory"></param>
        public DefaultCommitEventService(
            IExecutedCommandService executedCommandService,
            IAggregateRootTypeCodeProvider aggregateRootTypeCodeProvider,
            IAggregateRootFactory aggregateRootFactory,
            IEventStreamConvertService eventStreamConvertService,
            IEventSourcingService eventSourcingService,
            IMemoryCache memoryCache,
            IAggregateStorage aggregateStorage,
            IRetryCommandService retryCommandService,
            IEventStore eventStore,
            IEventPublisher eventPublisher,
            IActionExecutionService actionExecutionService,
            IEventSynchronizerProvider eventSynchronizerProvider,
            ILoggerFactory loggerFactory)
        {
            _executedCommandService = executedCommandService;
            _aggregateRootTypeCodeProvider = aggregateRootTypeCodeProvider;
            _aggregateRootFactory = aggregateRootFactory;
            _eventStreamConvertService = eventStreamConvertService;
            _eventSourcingService = eventSourcingService;
            _memoryCache = memoryCache;
            _aggregateStorage = aggregateStorage;
            _retryCommandService = retryCommandService;
            _eventStore = eventStore;
            _eventPublisher = eventPublisher;
            _actionExecutionService = actionExecutionService;
            _eventSynchronizerProvider = eventSynchronizerProvider;
            _logger = loggerFactory.Create(GetType().FullName);
        }

        #endregion

        /// <summary>Set the command executor.
        /// </summary>
        /// <param name="commandExecutor"></param>
        public void SetCommandExecutor(ICommandExecutor commandExecutor)
        {
            _retryCommandService.SetCommandExecutor(commandExecutor);
        }
        /// <summary>Commit the dirty aggregate's domain events to the eventstore and publish the domain events.
        /// </summary>
        public void CommitEvent(EventProcessingContext context)
        {
            var commitEvents = new Func<bool>(() =>
            {
                try
                {
                    return CommitEvents(context);
                }
                catch (Exception ex)
                {
                    _logger.Error(string.Format("Exception raised when committing events:{0}.", context.EventStream), ex);
                    return false;
                }
            });

            _actionExecutionService.TryAction("CommitEvents", commitEvents, 3, null);
        }

        #region Private Methods

        private bool CommitEvents(EventProcessingContext context)
        {
            var synchronizerResult = SyncBeforeEventPersisting(context.EventStream);

            switch (synchronizerResult.Status)
            {
                case SynchronizeStatus.SynchronizerConcurrentException:
                    return false;
                case SynchronizeStatus.Failed:

                    NotifyCommandExecuted(context, CommandStatus.Failed, synchronizerResult.ExceptionTypeName, synchronizerResult.ErrorMessage);
                    return true;
                default:
                {
                    var persistEventsCallback = new Func<object, bool>(obj =>
                    {
                        var currentContext = obj as EventProcessingContext;
                        var eventStream = currentContext.EventStream;

                        if (currentContext.AppendResult == EventAppendResult.Success)
                        {
                            RefreshMemoryCache(currentContext);
                            SyncAfterEventPersisted(eventStream);
                            PublishEvents(currentContext, eventStream);
                            NotifyCommandExecuted(currentContext, CommandStatus.Success, null, null);
                        }
                        else if (currentContext.AppendResult == EventAppendResult.DuplicateCommit)
                        {
                            var existingEventStream = GetEventStream(eventStream.AggregateRootId, eventStream.CommitId);
                            if (existingEventStream != null)
                            {
                                SyncAfterEventPersisted(existingEventStream);
                                PublishEvents(currentContext, existingEventStream);
                                NotifyCommandExecuted(currentContext, CommandStatus.Success, null, null);
                            }
                            else
                            {
                                var errorMessage = string.Format("Duplicate commit, but can't find the existing eventstream from eventstore. commandId:{0}, aggregateRootId:{1}, aggregateRootTypeCode:{2}",
                                    eventStream.CommitId,
                                    eventStream.AggregateRootId,
                                    eventStream.AggregateRootTypeCode);
                                _logger.Error(errorMessage);
                                NotifyCommandExecuted(currentContext, CommandStatus.Failed, null, errorMessage);
                            }
                        }
                        else if (currentContext.Exception != null)
                        {
                            if (currentContext.Exception is DuplicateAggregateException)
                            {
                                NotifyCommandExecuted(currentContext, CommandStatus.Failed, currentContext.Exception.GetType().Name, currentContext.Exception.Message);
                            }
                            else if (currentContext.Exception is ConcurrentException)
                            {
                                RefreshMemoryCacheFromEventStore(eventStream);
                                RetryCommand(currentContext);
                            }
                        }
                        return true;
                    });

                    PersistEvents(context, new ActionInfo("PersistEventsCallback", persistEventsCallback, context, null));
                    return true;
                }
            }
        }
        private void PersistEvents(EventProcessingContext context, ActionInfo successCallback)
        {
            var persistEvents = new Func<bool>(() =>
            {
                try
                {
                    context.AppendResult = _eventStore.Append(_eventStreamConvertService.ConvertTo(context.EventStream));
                    _logger.DebugFormat("Persist event stream success. {0}", context.EventStream);
                    return true;
                }
                catch (Exception ex)
                {
                    if (ex is ConcurrentException || ex is DuplicateAggregateException)
                    {
                        context.Exception = ex as ENodeException;
                        return true;
                    }
                    _logger.Error(string.Format("{0} raised when persisting events:{1}", ex.GetType().Name, context.EventStream), ex);
                    return false;
                }
            });

            _actionExecutionService.TryAction("PersistEvents", persistEvents, 3, successCallback);
        }
        private void RefreshMemoryCache(EventProcessingContext context)
        {
            try
            {
                _eventSourcingService.ReplayEvents(context.AggregateRoot, new EventStream[] { context.EventStream });
                _memoryCache.Set(context.AggregateRoot);
                _logger.DebugFormat("Refreshed memory cache, aggregateRootType:{0}, aggregateRootId:{1}, aggregateRootVersion:{2}", context.AggregateRoot.GetType().Name, context.AggregateRoot.UniqueId, context.AggregateRoot.Version);
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Exception raised when refreshing memory cache by event stream:{0}", context.EventStream), ex);
            }
        }
        private void RefreshMemoryCacheFromEventStore(EventStream eventStream)
        {
            try
            {
                var aggregateRootType = _aggregateRootTypeCodeProvider.GetType(eventStream.AggregateRootTypeCode);
                if (aggregateRootType == null)
                {
                    _logger.ErrorFormat("Could not find aggregate root type by aggregate root type code [{0}].", eventStream.AggregateRootTypeCode);
                    return;
                }
                var aggregateRoot = _aggregateStorage.Get(aggregateRootType, eventStream.AggregateRootId);
                if (aggregateRoot != null)
                {
                    _memoryCache.Set(aggregateRoot);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Exception raised when refreshing memory cache from eventstore, current eventStream:{0}", eventStream), ex);
            }
        }
        private void PublishEvents(EventProcessingContext eventProcessingContext, EventStream eventStream)
        {
            var publishEvents = new Func<bool>(() =>
            {
                try
                {
                    _eventPublisher.PublishEvent(eventProcessingContext.ProcessingCommand.CommandExecuteContext.Items, eventStream);
                    _logger.DebugFormat("Publish event stream success. {0}", eventStream);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(string.Format("Exception raised when publishing events:{0}", eventStream), ex);
                    return false;
                }
            });

            var publishEventsCallback = new Func<object, bool>(obj =>
            {
                var currentContext = obj as EventProcessingContext;
                currentContext.ProcessingCommand.CommandExecuteContext.OnCommandExecuted(
                    currentContext.ProcessingCommand.Command,
                    CommandStatus.Success,
                    currentContext.AggregateRoot.UniqueId,
                    null,
                    null);
                return true;
            });

            _actionExecutionService.TryAction("PublishEvents", publishEvents, 3, new ActionInfo("PublishEventsCallback", publishEventsCallback, eventProcessingContext, null));
        }
        private SynchronizeResult SyncBeforeEventPersisting(EventStream eventStream)
        {
            var result = new SynchronizeResult { Status = SynchronizeStatus.Success };

            foreach (var evnt in eventStream.Events)
            {
                var synchronizers = _eventSynchronizerProvider.GetSynchronizers(evnt.GetType());
                foreach (var synchronizer in synchronizers)
                {
                    try
                    {
                        synchronizer.OnBeforePersisting(evnt);
                    }
                    catch (ConcurrentException)
                    {
                        result.Status = SynchronizeStatus.SynchronizerConcurrentException;
                        return result;
                    }
                    catch (Exception ex)
                    {
                        var errorMessage = string.Format("{0} raised when calling synchronizer's OnBeforePersisting method. synchronizer:{1}, events:{2}, errorMessage:{3}",
                            ex.GetType().Name,
                            synchronizer.GetInnerSynchronizer().GetType().Name,
                            eventStream,
                            ex.Message);
                        _logger.Error(errorMessage, ex);
                        result.Status = SynchronizeStatus.Failed;
                        result.ExceptionTypeName = ex.GetType().Name;
                        result.ErrorMessage = ex.Message;
                        return result;
                    }
                }
            }

            return result;
        }
        private void SyncAfterEventPersisted(EventStream eventStream)
        {
            foreach (var evnt in eventStream.Events)
            {
                var synchronizers = _eventSynchronizerProvider.GetSynchronizers(evnt.GetType());
                foreach (var synchronizer in synchronizers)
                {
                    try
                    {
                        synchronizer.OnAfterPersisted(evnt);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(string.Format(
                            "Exception raised when calling synchronizer's OnAfterPersisted method. synchronizer:{0}, events:{1}",
                            synchronizer.GetInnerSynchronizer().GetType().Name,
                            eventStream), ex);
                    }
                }
            }
        }
        private void RetryCommand(EventProcessingContext context)
        {
            if (!_retryCommandService.RetryCommand(context.ProcessingCommand))
            {
                var command = context.ProcessingCommand.Command;
                var errorMessage = string.Format("{0} [id:{1}, aggregateId:{2}] retried count reached to its max retry count {3}.", command.GetType().Name, command.Id, context.EventStream.AggregateRootId, command.RetryCount);
                NotifyCommandExecuted(context, CommandStatus.Failed, null, errorMessage);
            }
        }
        private void NotifyCommandExecuted(EventProcessingContext context, CommandStatus commandStatus, string exceptionTypeName, string errorMessage)
        {
            _executedCommandService.ProcessExecutedCommand(
                context.ProcessingCommand.CommandExecuteContext,
                context.ProcessingCommand.Command,
                commandStatus,
                context.EventStream.AggregateRootId,
                exceptionTypeName,
                errorMessage);
        }
        private EventStream GetEventStream(string aggregateRootId, string commitId)
        {
            return _eventStreamConvertService.ConvertFrom(_eventStore.Find(aggregateRootId, commitId));
        }

        #endregion

        class SynchronizeResult
        {
            public SynchronizeStatus Status { get; set; }
            public string ExceptionTypeName { get; set; }
            public string ErrorMessage { get; set; }
        }
        enum SynchronizeStatus
        {
            Success,
            SynchronizerConcurrentException,
            Failed
        }
    }
}
