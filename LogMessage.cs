using log4net.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Log4Net.Helper
{
    internal class LogMessage
    {
        private readonly object _message;
        private readonly LocationInfo _locationInfo;
        private readonly string _thread;
        private readonly string _service;

        public object Message
        {
            get { return _message; }
        }
        public LocationInfo LocationInfo
        {
            get { return _locationInfo; }
        }
        public string Thread
        {
            get { return _thread; }
        }
        public string Service
        {
            get { return _service; }
        }

        public LogMessage(object message) : this(message, null, null, null)
        {
        }

        public LogMessage(object message, LocationInfo locationInfo, string thread) : this(message, locationInfo, thread, null)
        {
        }

        public LogMessage(object message, LocationInfo locationInfo, string thread, string service)
        {
            _message = message;
            _locationInfo = locationInfo;
            _thread = thread;
            _service = service;
        }

        public override string ToString()
        {
            if (Message is null)
                return string.Empty;
            else if (Message is string message)
                return message;
            else if (Message is JValue jvalue)
                return jvalue.ToString();
            else if (Message is JToken jtoken)
                return jtoken.ToString(Formatting.None);
            else if (Message.GetType().IsValueType)
                return Message.ToString();

            return JsonConvert.SerializeObject(Message);
        }
    }
}
