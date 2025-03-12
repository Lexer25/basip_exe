using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basip
{
    public class WorkerOptions
    {
        public string db_config { get; set; }
        public string timestart { get; set; }
        public string timeout { get; set; }
        public bool run_now { get; set; }
        public int time_wait_http { get; set; }
    }
}
