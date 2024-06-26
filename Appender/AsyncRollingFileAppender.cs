using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using log4net.Appender;
using log4net.Core;
using log4net.Util;

namespace Log4Net.Helper.Appender
{
    public class AsyncRollingFileAppender : RollingFileAppender
	{
		public override void ActivateOptions()
		{
			if (base.Threshold != Level.Off)
			{
				base.ActivateOptions();
				_pendingAppends = new RingBuffer<LoggingEvent>(QueueSizeLimit);
				_pendingAppends.BufferOverflow += OnBufferOverflow;
				StartAppendTask();
			}
		}

		public bool IsEnabledEvent(LoggingEvent loggingEvent)
		{
			return base.FilterEvent(loggingEvent);
        }

		protected override void Append(LoggingEvent[] loggingEvents)
		{
			Array.ForEach(loggingEvents, Append);
		}

		protected override void Append(LoggingEvent loggingEvent)
		{
			if (FilterEvent(loggingEvent))
			{
                _pendingAppends.Enqueue(loggingEvent);
            }
        }

        private LoggingEvent CreateNewLogginEvent(LoggingEvent loggingEvent)
        {
            if (loggingEvent == null)
                return loggingEvent;

            if (loggingEvent.MessageObject != null && loggingEvent.MessageObject is LogMessage)
            {
                var logMessage = loggingEvent.MessageObject as LogMessage;
                object messageObject = "[Thread " + logMessage.Thread + "] " + logMessage.ToString();
                loggingEvent = new LoggingEvent(
					typeof(LogHelper),
                    loggingEvent.Repository,
                    loggingEvent.LoggerName,
                    loggingEvent.Level,
                    messageObject,
                    loggingEvent.ExceptionObject
                );
            }
            return loggingEvent;
        }

        private void LogAppenderError(string logMessage, Exception exception)
        {
            try
            {
                var windowsIdentity = WindowsIdentity.GetCurrent();
                base.Append(new LoggingEvent(new LoggingEventData
                {
                    Level = Level.Error,
                    Message = "Appender exception: " + logMessage,
                    TimeStampUtc = DateTime.UtcNow,
                    Identity = "",
                    ExceptionString = exception.ToString(),
                    UserName = windowsIdentity != null ? windowsIdentity.Name : "",
                    Domain = AppDomain.CurrentDomain.FriendlyName,
                    ThreadName = Thread.CurrentThread.ManagedThreadId.ToString(),
                    LocationInfo = new LocationInfo(this.GetType().Name, "LogAppenderError", "AsyncRollingFileAppender.cs", "63"),
                    LoggerName = this.GetType().FullName,
                    Properties = new PropertiesDictionary(),
                }));
            }
            catch
            {
            }
        }

        private readonly ManualResetEvent _manualResetEvent;
		private int _bufferOverflowCounter;
		private bool _hasFinished;
		private DateTime _lastLoggedBufferOverflow;
		private bool _logBufferOverflow;
		private RingBuffer<LoggingEvent> _pendingAppends;
		private int _queueSizeLimit = 1000;
		private bool _shuttingDown;
		private bool _forceStop;

		public AsyncRollingFileAppender()
		{
			_manualResetEvent = new ManualResetEvent(false);
		}

		public int QueueSizeLimit
		{
			get { return _queueSizeLimit; }
			set { _queueSizeLimit = value; }
		}
		protected override void OnClose()
		{
			_shuttingDown = true;
			_manualResetEvent.WaitOne(TimeSpan.FromSeconds(5));

			if (!_hasFinished)
			{
				_forceStop = true;
				var windowsIdentity = WindowsIdentity.GetCurrent();

			    var logEvent = new LoggingEvent(new LoggingEventData
			        {
			            Level = global::log4net.Core.Level.Error,
			            Message = "Unable to clear out the AsyncRollingFileAppender buffer in the allotted time, forcing a shutdown",
			            TimeStampUtc = DateTime.UtcNow,
			            Identity = "",
			            ExceptionString = "",
			            UserName = windowsIdentity != null ? windowsIdentity?.Name : "",
			            Domain = AppDomain.CurrentDomain.FriendlyName,
			            ThreadName = Thread.CurrentThread.ManagedThreadId.ToString(),
			            LocationInfo = new LocationInfo(this.GetType().Name, "OnClose", "AsyncRollingFileAppender.cs", "108"),
			            LoggerName = this.GetType().FullName,
			            Properties = new PropertiesDictionary(),
			        });

			    if (this.DateTimeStrategy != null)
			    {
                    try
                    {
                        base.Append(logEvent);
                    }
                    catch
                    {
                    }
			    }			    
			}

			base.OnClose();
		}

		private void StartAppendTask()
		{
			if (!_shuttingDown)
			{
				Task appendTask = new Task(AppendLoggingEvents, TaskCreationOptions.LongRunning);
				appendTask.ContinueWith(t => LogErrorsInner(t, LogAppenderError), TaskContinuationOptions.OnlyOnFaulted).ContinueWith(x => StartAppendTask()).ContinueWith(t => LogErrorsInner(t, LogAppenderError), TaskContinuationOptions.OnlyOnFaulted);
				appendTask.Start();
			}
		}

		private static void LogErrorsInner(Task task, Action<string, Exception> logAction)
		{
			if (task.Exception != null)
			{
				logAction("Aggregate Exception with " + task.Exception.InnerExceptions.Count + " inner exceptions: ", task.Exception);
				foreach (var innerException in task.Exception.InnerExceptions)
				{
					logAction("Inner exception from aggregate exception: ", innerException);
				}
			}
		}

		private void AppendLoggingEvents()
		{
			LoggingEvent loggingEventToAppend;
			while (!_shuttingDown)
			{
				if (_logBufferOverflow)
				{
					LogBufferOverflowError();
					_logBufferOverflow = false;
					_bufferOverflowCounter = 0;
					_lastLoggedBufferOverflow = DateTime.UtcNow;
				}

				while (!_pendingAppends.TryDequeue(out loggingEventToAppend))
				{
					Thread.Sleep(10);
					if (_shuttingDown)
					{
						break;
					}
				}
				if (loggingEventToAppend == null)
				{
					continue;
				}

				try
				{
					base.Append(CreateNewLogginEvent(loggingEventToAppend));
				}
				catch
				{
				}
			}

			while (_pendingAppends.TryDequeue(out loggingEventToAppend) && !_forceStop)
			{
				try
				{
					base.Append(CreateNewLogginEvent(loggingEventToAppend));
				}
				catch
				{
				}
			}
			_hasFinished = true;
			_manualResetEvent.Set();
		}

		private void LogBufferOverflowError()
		{
            try
            {
                var windowsIdentity = WindowsIdentity.GetCurrent();
                base.Append(new LoggingEvent(new LoggingEventData
                {
                    Level = Level.Error,
                    Message = string.Format(
                                "Buffer overflow. {0} logging events have been lost in the last 30 seconds. [QueueSizeLimit: {1}]",
                                _bufferOverflowCounter,
                                QueueSizeLimit),
                    TimeStampUtc = DateTime.UtcNow,
                    Identity = "",
                    ExceptionString = "",
                    UserName = windowsIdentity != null ? windowsIdentity.Name : "",
                    Domain = AppDomain.CurrentDomain.FriendlyName,
                    ThreadName = Thread.CurrentThread.ManagedThreadId.ToString(),
                    LocationInfo = new LocationInfo(this.GetType().Name, "LogBufferOverflowError", "AsyncRollingFileAppender.cs", "219"),
                    LoggerName = this.GetType().FullName,
                    Properties = new PropertiesDictionary(),
                }));
            }
            catch
            {
            }
		}

		private void OnBufferOverflow(object sender, EventArgs eventArgs)
		{
			_bufferOverflowCounter++;
			if (_logBufferOverflow == false)
			{
				if (_lastLoggedBufferOverflow < DateTime.UtcNow.AddSeconds(-30))
				{
					_logBufferOverflow = true;
				}
			}
		}

		private class RingBuffer<T>
		{
			private readonly object _lockObject = new object();
			private readonly T[] _buffer;
			private readonly int _size;
			private int _readIndex = 0;
			private int _writeIndex = 0;
			private bool _bufferFull = false;

			public event Action<object, EventArgs> BufferOverflow;

			public RingBuffer(int size)
			{
				this._size = size;
				_buffer = new T[size];
			}

			public void Enqueue(T item)
			{
				lock (_lockObject)
				{
					_buffer[_writeIndex] = item;
					_writeIndex = (++_writeIndex) % _size;
					if (_bufferFull)
					{
                        BufferOverflow?.Invoke(this, EventArgs.Empty);
                        _readIndex = _writeIndex;
					}
					else if (_writeIndex == _readIndex)
					{
						_bufferFull = true;
					}
				}
			}

			public bool TryDequeue(out T ret)
			{
				if (_readIndex == _writeIndex && !_bufferFull)
				{
					ret = default;
					return false;
				}
				lock (_lockObject)
				{
					if (_readIndex == _writeIndex && !_bufferFull)
					{
						ret = default;
						return false;
					}

					ret = _buffer[_readIndex];
					_readIndex = (++_readIndex) % _size;
					_bufferFull = false;
					return true;
				}
			}
		}
    }
}