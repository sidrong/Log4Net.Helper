using log4net.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Log4Net.Helper
{
    public class LogRouter
    {
        private string _service;
        private object message;

        public string Service
        {
            get
            {
                return _service;
            }
            set
            {
                _service = value?.Replace('.', '_');
            }
        }

        public object Message
        {
            get
            {
                return message;
            }
            set
            {
                message = value;
            }
        }
    }
}
