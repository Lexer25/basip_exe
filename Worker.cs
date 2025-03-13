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
            DataRowCollection data = db.GetDevice().Rows;//получить список панелей, сразу с логинами и паролями
            con.Close();
            foreach (DataRow row in data)
                tasks.Add(TaskGet(row));//async Начинаю асинхронные процессы
                //TaskGet(new Device(row), db).Wait();//not sync
            logger.LogDebug("device: "+data.Count);
            Task.WaitAll(tasks.ToArray());
            logger.LogDebug("time: "+stopwatch.ElapsedMilliseconds);
        }

        /* 12.03.2025 
         * Освной процесс работы с панелью
         * 
         * 
         */
        private async Task TaskGet(DataRow row)// в row содержатся логин, пароль, id_dev, IP адрес
        {
            DB db = new DB();
            FbConnection con = db.DBconnect(options.db_config);
            con.Open();
            Device dev =new Device(row);

            DeviceInfo deviceInfo= await dev.GetInfo(options.time_wait_http);//нужна ли тут сущность DeviceInfo? может, это включить в класс Device?
            if (dev.is_online)// связи с панелью нет
            {
                logger.LogDebug($@"No connect: id: {row["id_dev"]} ip: {dev.ip}");
                //db.saveParam((int)row["id_dev"], "ABOUT", null, "no connect");
                db.saveParam((int)row["id_dev"], "ONLINE", 0, null);//зафиксировать отсутствие связи с панелью.
                db.updateCaridxErrAll((int)row["id_dev"], "Нет связи с устройством");//поставить отметку для всех карт этой панели
                return; 
            }
            else //связь с панелью есть
            {
                //начинаю обработку карт для записи
                string data = $@"{deviceInfo.device_model} , {deviceInfo.firmware_version} , {deviceInfo.firmware_version} , {deviceInfo.api_version}";
                logger.LogDebug((int)row["id_dev"] +" | "+data);
                db.saveParam((int)row["id_dev"], "ABOUT", null, data);//фиксирую информацию о панели.
                db.saveParam((int)row["id_dev"], "ONLINE", 1, null);//фиксирую наличие связи
                //провожу авторизацию
                
                if(dev.Auth(dev.password))
                {
                    //проверка очереди на запись карт
                    DataRowCollection cardList = db.GetCardForLoad((int)row["id_dev"]);//получить список карт для панели
                    if (cardList.Count>0)//если карт есть, то начинаем работу с картами
                    {
                        //запись идентификаторов в вызывную панель
                        foreach(DataRow card in cardList)
                        {
                            //
                            if(card.attempt == 1)//запись идентификатора в панель
                            {
                                if(dev.writekey(card.id))//если запись прошла успешно, то 
                                {
                                    db.delFromCardindev((int)row["id_dev"], card.id);//удалить карту из очереди загрузки
                                    db.updateCaridxOk((int)row["id_dev"], card.id);//записать в таблицу cardidx дату и время успешной записи

                                } else
                                {
                                    string mess = "Причина неудачной записи.";
                                    db.incrementCardindev((int)row["id_dev"], card.id);//количество попыток увеличть на 1.
                                    db.updateCaridxErr((int)row["id_dev"], card.id, mess);//зафиксировать неуспешную попытку записи.

                                }

                            }
                            if(card.attempt == 2)//удаление идентификатор из панели
                            {
                                db.delFromCardindev((int)row["id_dev"], card.id);//удалить карту из таблицы cardindev
                                //с таблицей cardidx работа не ведется, т.к. там информации о картах уже нет
                                
                            } else
                            {
                                db.incrementCardindev((int)row["id_dev"], card.id);
                                //с таблицей cardidx работа не ведется, т.к. там информации о картах уже нет
                            }

                        }

                    } else
                    {
                        //нет очереди. возможно, это надо зафиксировать в лог-файле.
                    }

                    //сбор событий
                    /*
                     * тут надо организовать цикл выбора событий из панели и запись событий в БД СКУД.
                     * 
                     * 
                     */
                } else //авторизация прошла неуспешно
                {
                    //сохранить в лог запись о неудачной авторизации.
                    db.updateCaridxErrAll((int)row["id_dev"], card.id, "Ошибка авторизации.");//поставить отметку для всех карт этой панели

                }

            }
            con.Close();
            //  Thread.Sleep(1000);
            // logger.LogDebug(dev.ip.ToString()+" | "+dev.id_dev_door0 + " | " +dev.id_dev_door1+" | "+dev.name+ " | " + dev.id_ctrl);
        }
    }
}
