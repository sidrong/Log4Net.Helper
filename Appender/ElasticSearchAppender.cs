using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Threading;
using System.Data.Common;

using log4net.Appender;
using log4net.Core;
using Log4Net.Helper.Tools;
using System.Threading.Tasks;

namespace Log4Net.Helper.Appender
{
    public class ElasticSearchAppender : BufferingAppenderSkeleton
	{
        private static readonly string AppenderType = typeof(ElasticSearchAppender).Name;
        private const int DefaultOnCloseTimeout = 30000;
        private readonly ManualResetEvent workQueueEmptyEvent;
        private int queuedCallbackCount;
        private IRepository repository;
        private bool shuttingDown;
        private static DateTime sendEmailDate = DateTime.Today;
        private static int sendEmailTimes = 0;

        public ElasticSearchAppender()
        {
            workQueueEmptyEvent = new ManualResetEvent(true);
            OnCloseTimeout = DefaultOnCloseTimeout;
        }

        public override void ActivateOptions()
        {
            if (base.Threshold != Level.Off)
            {
                base.ActivateOptions();
                ServicePointManager.Expect100Continue = false;
                try
                {
                    Validate(this.LogRepository, this.ConnectionString);
                }
                catch (Exception exception)
                {
                    this.HandleError("Failed to validate LogRepository or ConnectionString in ActivateOptions", Level.Error, exception);
                    return;
                }
                this.ConnectionString += $";BufferSize={base.BufferSize}";
                this.repository = this.CreateRepository(this.LogRepository, this.ConnectionString);
            }
        }

        private void BeginAsyncSend()
        {
            this.workQueueEmptyEvent.Reset();
            Interlocked.Increment(ref this.queuedCallbackCount);
        }

        protected virtual IRepository CreateRepository(string logRepository, string connectionString) =>
            Repository.Create(logRepository, connectionString);

        private void EndAsyncSend()
        {
            if (Interlocked.Decrement(ref this.queuedCallbackCount) <= 0)
            {
                this.workQueueEmptyEvent.Set();
            }
        }

        private void HandleError(object message, Level level, Exception ex = null)
        {
            if (!shuttingDown && Utils.LogToFile(message, level, ex))
                return;
            this.ErrorHandler.Error(string.Format("{0} [{1}]: {2}.", AppenderType, base.Name, message), ex, ErrorCode.GenericFailure);
        }

        protected override void OnClose()
        {
            shuttingDown = true;
            base.OnClose();
            if (!this.TryWaitAsyncSendFinish())
            {
                this.HandleError("Failed to send all queued events before logger close", Level.Error);
            }
        }

        protected override void SendBuffer(LoggingEvent[] events)
        {
            if (this.repository == null)
                return;
            this.BeginAsyncSend();
            Task.Factory.StartNew(SendBufferCallback, LogEvent.CreateMany(events)).ContinueWith(t =>
            {
                this.EndAsyncSend();
                this.HandleError("Failed to async send logging events in SendBuffer", Level.Error, t.Exception);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void SendBufferCallback(object state)
        {
            try
            {
                DateTime now = DateTime.Now;
                
                this.repository.Add((IEnumerable<LogEvent>)state, base.BufferSize);

                double ms = (DateTime.Now - now).TotalMilliseconds;
                if (Alert.LocalAlertMs > 0 && ms > Alert.LocalAlertMs)
                {
                    HandleError(string.Format("Take too much time({0}ms) to push data to elasticsearch", ms), Level.Warn);
                }
                if (Alert.EmailAlertMs > 0 && ms > Alert.EmailAlertMs &&
                    !string.IsNullOrWhiteSpace(Alert.SmptHost) && !string.IsNullOrWhiteSpace(Alert.EmailFrom) && !string.IsNullOrWhiteSpace(Alert.EmailTo))
                {
                    if (sendEmailDate != DateTime.Today)
                    {
                        sendEmailDate = DateTime.Today;
                        sendEmailTimes = 0;
                    }
                    if (sendEmailTimes < 3)
                    {
                        Utils.SendEmailAlert(new { message = string.Format("Take too much time({0}ms) to push data to elasticsearch", ms), logInfo = state },
                            Level.Warn, Alert.SmptHost, Alert.EmailFrom, Alert.EmailTo, Alert.UserName, Alert.Password, Alert.Domain, Alert.IsAsync);
                        sendEmailTimes++;
                    }
                }
                this.EndAsyncSend();
            }
            catch (Exception exception)
            {
                this.EndAsyncSend();
                this.HandleError("Failed to add logEvents to Repository in SendBufferCallback", Level.Error, exception);
            }
        }

        protected virtual bool TryWaitAsyncSendFinish() =>
            this.workQueueEmptyEvent.WaitOne(this.OnCloseTimeout, false);

        private static void Validate(string logRepository, string connectionString)
        {
            if (logRepository is null)
            {
                throw new ArgumentNullException("LogRepository");
            }
            if (logRepository.Length == 0)
            {
                throw new ArgumentException("LogRepository is empty", "LogRepository");
            }
            if (!new Regex(@"^[a-z0-9]+[a-z0-9\-_]*$").IsMatch(logRepository))
            {
                throw new Exception("LogRepository does not conform to the naming convention");
            }
            if (connectionString is null)
            {
                throw new ArgumentNullException("ConnectionString");
            }
            if (connectionString.Length == 0)
            {
                throw new ArgumentException("ConnectionString is empty", "ConnectionString");
            }
        }

        public string ConnectionString { get; set; }

        public string LogRepository { get; set; }

        public int OnCloseTimeout { get; set; }

        public AlertSetting Alert { get; set; }
    }


    public interface IRepository
    {
        void Add(IEnumerable<LogEvent> logEvents, int bufferSize);
    }


    public class Repository : IRepository
    {
        private readonly Uri uri;
        private readonly IHttpClient httpClient;

        private Repository(Uri uri, IHttpClient httpClient)
        {
            this.uri = uri;
            this.httpClient = httpClient;
        }

        public void Add(IEnumerable<LogEvent> logEvents, int bufferSize)
        {
            if (bufferSize <= 1)
            {
                foreach(var logEvent in logEvents)
                {
                    this.httpClient.Post((Uri)this.uri, logEvent);
                }
            }
            else
            {
                this.httpClient.PostBulk((Uri)this.uri, logEvents);
            }
        }

        public static IRepository Create(string logRepository, string connectionString) =>
            Create(logRepository, connectionString, new HttpClient());

        public static IRepository Create(string logRepository, string connectionString, IHttpClient httpClient) =>
            new Repository(Uri.For(logRepository, connectionString), httpClient);
    }

    public interface IHttpClient
    {
        void Post(Uri uri, LogEvent item);
        void PostBulk(Uri uri, IEnumerable<LogEvent> items);
    }

    public class HttpClient : IHttpClient
    {
        private static readonly string ContentType = "application/json";
        private static readonly string Method = "POST";

        private static StreamWriter GetRequestStream(WebRequest httpWebRequest) =>
            new StreamWriter(httpWebRequest.GetRequestStream());

        public void Post(Uri uri, LogEvent item)
        {
            string subIndex = item.Service;
            HttpWebRequest httpWebRequest = RequestFor(uri.ToSystemUri(subIndex));
            httpWebRequest.Proxy = null;
            using (StreamWriter writer = GetRequestStream(httpWebRequest))
            {
                string logInfo = new LogMessage(item.LogInfo).ToString();
                writer.Write(logInfo);
                writer.Flush();
                HttpWebResponse response = (HttpWebResponse)httpWebRequest.GetResponse();
                HttpStatusCode statusCode = response.StatusCode;
                response.Close();
                if (statusCode != HttpStatusCode.Created)
                {
                    throw new WebException(string.Format("Failed to post {0} to {1}.", logInfo, uri));
                }
            }
        }

        public void PostBulk(Uri uri, IEnumerable<LogEvent> items)
        {
            string subIndex = null;
            StringBuilder builder = new StringBuilder();
            foreach (LogEvent event2 in items)
            {
                subIndex = event2.Service;
                builder.AppendLine("{\"index\" : {} }");
                builder.AppendLine(new LogMessage(event2.LogInfo).ToString());
            }
            HttpWebRequest httpWebRequest = RequestFor(uri.ToSystemUri(subIndex));
            httpWebRequest.Proxy = null;
            using (StreamWriter writer = GetRequestStream(httpWebRequest))
            {
                writer.Write(builder.ToString());
                writer.Flush();
                HttpWebResponse response = (HttpWebResponse)httpWebRequest.GetResponse();
                HttpStatusCode statusCode = response.StatusCode;
                response.Close();
                if (statusCode != HttpStatusCode.Created && statusCode != HttpStatusCode.OK)
                {
                    throw new WebException(string.Format("Failed to post {0} to {1}.", builder.ToString(), uri));
                }
            }
        }

        public static HttpWebRequest RequestFor(System.Uri uri)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.ContentType = ContentType;
            request.Method = Method;
            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                request.Headers.Remove(HttpRequestHeader.Authorization);
                request.Headers.Add(HttpRequestHeader.Authorization, "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(uri.UserInfo)));
            }
            return request;
        }
    }

    public class LogEvent
    {
        private string _service;
        private LogInfo _logInfo;

        public string Service
        {
            get { return _service; }
        }

        public LogInfo LogInfo
        {
            get { return _logInfo; }
        }

        private static LogEvent Create(LoggingEvent loggingEvent)
        {
            LogInfo logInfo = new LogInfo
            {
                LoggerName = loggingEvent.LoggerName,
                Domain = loggingEvent.Domain,
                ThreadName = loggingEvent.ThreadName,
                TimeStamp = loggingEvent.TimeStamp.ToUniversalTime().ToString("O"),
                Exception = (loggingEvent.ExceptionObject == null) ? new object() : JsonSerializableException.Create(loggingEvent.ExceptionObject),
                Message = loggingEvent.RenderedMessage,
                Level = loggingEvent.Level?.DisplayName
            };

            string service = null;
            object messageObject;
            if (loggingEvent.MessageObject is LogMessage logMessage)
            {
                messageObject = logMessage.Message;
                logInfo.LocationInfo = loggingEvent.LocationInformation?.FullInfo;
                logInfo.ThreadName = logMessage.Thread;
                service = logMessage.Service;
            }
            else
            {
                messageObject = loggingEvent.MessageObject;
            }

            logInfo.MessageObject = messageObject == null || messageObject.GetType() == typeof(string) ? new object() : !(messageObject is Exception exception) ? messageObject : JsonSerializableException.Create(exception);

            return new LogEvent() { _logInfo = logInfo, _service = service };
        }

        public static IEnumerable<LogEvent> CreateMany(IEnumerable<LoggingEvent> loggingEvents)
        {
            List<LogEvent> logEvents = new List<LogEvent>();
            foreach (var e in loggingEvents)
            {
                logEvents.Add(Create(e));
            }
            return logEvents.ToArray();
        }
    }

    public class LogInfo
    {
        public string TimeStamp { get; set; }

        public string Message { get; set; }

        public object MessageObject { get; set; }

        public object Exception { get; set; }

        public string LoggerName { get; set; }

        public string Domain { get; set; }

        public string Level { get; set; }

        public string LocationInfo { get; set; }

        public string ThreadName { get; set; }
    }

    public class JsonSerializableException
    {
        public static JsonSerializableException Create(Exception ex)
        {
            JsonSerializableException exception2;
            if (ex is null)
            {
                exception2 = null;
            }
            else
            {
                JsonSerializableException exception1 = new JsonSerializableException
                {
                    Type = ex.GetType().FullName,
                    Message = ex.Message,
                    HelpLink = ex.HelpLink,
                    Source = ex.Source,
                    HResult = ex.HResult,
                    StackTrace = ex.StackTrace,
                    Data = ex.Data
                };
                JsonSerializableException exception = exception1;
                if (ex.InnerException != null)
                {
                    exception.InnerException = Create(ex.InnerException);
                }
                exception2 = exception;
            }
            return exception2;
        }

        public string Type { get; set; }

        public string Message { get; set; }

        public string HelpLink { get; set; }

        public string Source { get; set; }

        public int HResult { get; set; }

        public string StackTrace { get; set; }

        public System.Collections.IDictionary Data { get; set; }

        public JsonSerializableException InnerException { get; set; }
    }

    public class Uri
    {
        private string typeName;
        private readonly string logRepository;
        private readonly StringDictionary connectionStringParts;

        private Uri(string logRepository, StringDictionary connectionStringParts)
        {
            typeName = "_doc";
            this.logRepository = logRepository;
            this.connectionStringParts = connectionStringParts;
        }

        private string Bulk() =>
            (Convert.ToInt32(this.connectionStringParts[Keys.BufferSize]) <= 1) ? string.Empty : "/_bulk";

        private static StringDictionary GetConnectionStringParts(string connectionString)
        {
            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            var parts = new StringDictionary();
            foreach (string key in builder.Keys)
            {
                parts[key] = Convert.ToString(builder[key]);
            }
            return parts;
        }

        public static Uri For(string logRepository, string connectionString) =>
            new Uri(logRepository, GetConnectionStringParts(connectionString));

        private string Index(string subIndex)
        {
            string text1;
            string str = this.logRepository;
            if (!string.IsNullOrWhiteSpace(subIndex))
            {
                subIndex = subIndex.ToLower();
                if (new Regex(@"^[a-z0-9]+[a-z0-9\-_]*$").IsMatch(subIndex))
                {
                    str += (str.EndsWith("-") ? "" : "-") + subIndex;
                }
            }
            if (!IsRollingIndex(this.connectionStringParts))
            {
                text1 = str;
            }
            else
            {
                text1 = string.Format("{0}-{1}", str, this.GetRollingFormat());
            }
            return text1;
        }

        private bool IsRollingIndex(StringDictionary parts) =>
            parts.ContainsKey(Keys.Rolling) && !string.IsNullOrWhiteSpace(parts[Keys.Rolling]) && bool.TryParse(parts[Keys.Rolling], out bool rolling) && rolling;

        private string GetRollingFormat()
        {
            string defFormat = "yyyyMMdd";
            try
            {
                return Clock.Date.ToString(this.connectionStringParts[Keys.RollingDateFormat] ?? defFormat);
            }
            catch
            {
                return Clock.Date.ToString(defFormat);
            }
        }

        public System.Uri ToSystemUri(string subIndex)
        {
            System.Uri uri2;
            if (!string.IsNullOrWhiteSpace(this.User()) && !string.IsNullOrWhiteSpace(this.Password()))
            {
                uri2 = new System.Uri($"{this.Scheme()}://{this.User()}:{this.Password()}@{this.Server()}:{this.Port()}/{this.Index(subIndex)}/{this.typeName}{this.Routing()}{this.Bulk()}");
            }
            else
            {
                System.Uri uri1;
                if (string.IsNullOrWhiteSpace(this.Port()))
                {
                    uri1 = new System.Uri($"{this.Scheme()}://{this.Server()}/{this.Index(subIndex)}/{this.typeName}{this.Routing()}{this.Bulk()}");
                }
                else
                {
                    uri1 = new System.Uri($"{this.Scheme()}://{this.Server()}:{this.Port()}/{this.Index(subIndex)}/{this.typeName}{this.Routing()}{this.Bulk()}");
                }
                uri2 = uri1;
            }
            return uri2;
        }

        private string Password() =>
            this.connectionStringParts[Keys.Password];

        private string Port() =>
            this.connectionStringParts[Keys.Port];

        private string Routing()
        {
            string str = this.connectionStringParts[Keys.Routing];
            return (string.IsNullOrWhiteSpace(str) ? string.Empty : $"?routing={str}");
        }

        private string Scheme() =>
            this.connectionStringParts[Keys.Scheme] ?? "http";

        private string Server() =>
            this.connectionStringParts[Keys.Server];

        private string User() =>
            this.connectionStringParts[Keys.User];

        private static class Keys
        {
            public const string Scheme = "Scheme";
            public const string User = "User";
            public const string Password = "Pwd";
            public const string Server = "Server";
            public const string Port = "Port";
            public const string Rolling = "Rolling";
            public const string RollingDateFormat = "RollingDateFormat";
            public const string BufferSize = "BufferSize";
            public const string Routing = "Routing";
        }
    }

    public class AnonymousDisposable : IDisposable
    {
        private readonly Action action;

        public AnonymousDisposable(Action action)
        {
            this.action = action;
        }

        public void Dispose()
        {
            this.action();
        }
    }

    public static class Clock
    {
        static DateTime? frozen;

        public static DateTime Date
        {
            get { return Now.Date; }
        }

        public static IDisposable Freeze(DateTime dateTime)
        {
            frozen = dateTime;

            return new AnonymousDisposable(() => Unfreeze());
        }

        public static DateTime Now
        {
            get { return frozen ?? DateTime.UtcNow; }
        }

        static void Unfreeze()
        {
            frozen = null;
        }
    }

    public class AlertSetting
    {
        public int LocalAlertMs { get; set; }
        public int EmailAlertMs { get; set; }
        public string SmptHost { get; set; }
        public string EmailFrom { get; set; }
        public string EmailTo { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
        public string Domain { get; set; }
        public bool IsAsync {  get; set; }
    }
}