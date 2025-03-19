using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RestSharp;
using Serilog.Core;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
        private async void run()
        {
            //logger.LogTrace(Device.CreateMD5("2644256"));//username=admin&password=2644256
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
            //DB db = new DB();
            //FbConnection con = db.DBconnect(options.db_config);
           // con.Open();
            Device dev =new Device(row,options.time_wait_http);
            dev.time_wait = 10;
            dev.base_url = "http://192.168.8.102:8888";
            dev.password = Device.CreateMD5("2644256");
            dev.login = "admin";
            JsonDocument deviceInfo= await dev.GetInfo();//нужна ли тут сущность DeviceInfo? может, это включить в класс Device?
            
            if (dev.is_online)// связи с панелью нет
            {
                if (await dev.Auth())
                {
                    DB db = new DB();
                    FbConnection con = db.DBconnect(options.db_config);
                    con.Open();
                    DataRowCollection cardList = db.GetCardForLoad((int)row["id_dev"]).Rows;//получить список карт для панели
                    logger.LogDebug("Card count: "+cardList.Count);
                    foreach (DataRow card in cardList)
                    {
                        switch ((int)card["operation"])
                        {
                            case 1:
                                RestResponse request = await dev.AddCard(card["id_card"].ToString());
                                switch (request.StatusCode)
                                {
                                    case HttpStatusCode.OK:
                                        var uid = JsonDocument.Parse(request.Content).RootElement.GetProperty("uid");
                                        logger.LogDebug("ok " + uid);
                                        break;
                                    case HttpStatusCode.BadRequest:
                                        logger.LogDebug("Card alredy exist cardid " + card["id_card"]);
                                        
                                        break;
                                    default:
                                        logger.LogDebug($@"add card write false status: {request.StatusCode} body: {request.Content} card: {card["id_card"]}");
                                        break;
                                }
                                break;
                            case 2:
                                RestResponse? content = await dev.GetInfoCard(card["id_card"].ToString());
                                switch (content.StatusCode)
                                {
                                    case HttpStatusCode.OK:
                                        foreach (JsonElement element in JsonDocument.Parse(content.Content).RootElement.GetProperty("list_items").EnumerateArray())
                                        {
                                            string uid_card = element.GetProperty("identifier_uid").ToString();
                                            if ((await dev.DeleteCard(uid_card)).StatusCode == HttpStatusCode.OK)
                                            {
                                                logger.LogDebug($@"Card {card["id_card"]} uid:{uid_card} delete");
                                            }
                                            else
                                            {
                                                logger.LogDebug($@"Card {card["id_card"]} uid:{uid_card} no delete");
                                            }
                                        }
                                        break;
                                    default:
                                        logger.LogCritical("Card exist but not found: " + card["id_card"]);
                                        break;
                                }
                                break;
                        }
                        
                    }
                    //db.saveParam((int)row["id_dev"], "ABOUT", null, "no connect");
                    // db.saveParam((int)row["id_dev"], "ONLINE", 0, null);//зафиксировать отсутствие связи с панелью.
                    //   db.updateCaridxErrAll((int)row["id_dev"], "Нет связи с устройством");//поставить отметку для всех карт этой панели
                    return;
                }
            }
            else
            {
                logger.LogDebug($@"device {dev.base_url} ofline");
            }
          /*  else //связь с панелью есть
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
                     *//*
                } else //авторизация прошла неуспешно
                {
                    //сохранить в лог запись о неудачной авторизации.
                    db.updateCaridxErrAll((int)row["id_dev"], card.id, "Ошибка авторизации.");//поставить отметку для всех карт этой панели

                }

            }*/
            //con.Close();
            //  Thread.Sleep(1000);
            // logger.LogDebug(dev.ip.ToString()+" | "+dev.id_dev_door0 + " | " +dev.id_dev_door1+" | "+dev.name+ " | " + dev.id_ctrl);
        }
    }
}
