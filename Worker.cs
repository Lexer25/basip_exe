using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

namespace Basip
{
    public class Worker : BackgroundService
    {
        public readonly ILogger logger;
        private WorkerOptions options;
        public TimeSpan timeout;
        public TimeSpan timestart;
        public TimeSpan deltasleep;
        public Worker(ILogger<Worker> logger, WorkerOptions options)
        {
            this.logger = logger;
            this.options = options;
            var time = options.timeout.Split(':');
            timeout = new TimeSpan(Int32.Parse(time[0]), Int32.Parse(time[1]), Int32.Parse(time[2]));
            time = options.timeout.Split(':');
            timestart = new TimeSpan(Int32.Parse(time[0]), Int32.Parse(time[1]), Int32.Parse(time[2]));
            var now = new TimeSpan(DateTime.Now.TimeOfDay.Hours, DateTime.Now.TimeOfDay.Minutes, DateTime.Now.TimeOfDay.Seconds);
            deltasleep = (options.run_now) ? TimeSpan.Zero :
                (timestart >= now) ? timestart - now : timestart - now + new TimeSpan(1, 0, 0, 0);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogTrace(@$"time run basip: {timestart} deltasleep: {deltasleep}");
            await Task.Delay(deltasleep);
            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogTrace($@"Старт итерации");
                run();
                logger.LogTrace($@"timeout basip: {timeout}");
                await Task.Delay(timeout, stoppingToken);
            }
        }
        private void run()
        {
            DB db = new DB();
            FbConnection con=db.DBconnect(options.db_config);
            try
            {
                con.Open();
            }catch (Exception e)
            {
                logger.LogError("No connect database :"+ options.db_config);
                return;
            }
            logger.LogTrace("Ok connect database");
            List<Task> tasks = new List<Task>();
            Stopwatch stopwatch = Stopwatch.StartNew();
            //запуск 
            DataRowCollection data = db.GetDevice().Rows;
            con.Close();
            foreach (DataRow row in data)
               tasks.Add(TaskGet(row));//async
                //TaskGet(new Device(row), db).Wait();//sync
            logger.LogDebug("device: "+data.Count);
            Task.WaitAll(tasks.ToArray());
            logger.LogDebug("time: "+stopwatch.ElapsedMilliseconds);
        }
        private async Task TaskGet(DataRow row)
        {
            DB db = new DB();
            FbConnection con = db.DBconnect(options.db_config);
            con.Open();
            Device dev =new Device(row);

            //   logger.LogDebug(dev.id_dev+"");
            // await Task.Delay(1000);
            //db.GetDevice();
            DeviceInfo deviceInfo= await dev.GetInfo(options.time_wait_http);
            if (deviceInfo == null)
            {
                logger.LogDebug($@"No connect: id: {dev.id_dev} ip: {dev.ip}");
                db.saveParam(dev.id_dev, "ABOUT", null, "no connect");
                db.saveParam(dev.id_dev, "ONLINE", 0, null);
            }
            else
            {
                string data = $@"{deviceInfo.device_model} , {deviceInfo.firmware_version} , {deviceInfo.firmware_version} , {deviceInfo.api_version}";
                logger.LogDebug(dev.id_dev+" | "+data);
                db.saveParam(dev.id_dev, "ABOUT", null, data);
                db.saveParam(dev.id_dev, "ONLINE", 1, null);
            }
            con.Close();
            //  Thread.Sleep(1000);
            // logger.LogDebug(dev.ip.ToString()+" | "+dev.id_dev_door0 + " | " +dev.id_dev_door1+" | "+dev.name+ " | " + dev.id_ctrl);
        }
    }
}
