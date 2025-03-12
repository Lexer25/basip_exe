using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basip
{
    internal class DeviceInfo
    {
        public string device_model { get; set; }  
        public string framework_version { get; set; }
        public string device_name { get; set; }        
        public string device_serial_number { get; set; }
        public string firmware_version { get; set; }
        public string commit {  get; set; } 
        public string device_type { get; set; }
        public string api_version { get; set; }
        public bool hybrid_enable { get; set; } 
    }
}
