using FirebirdSql.Data.FirebirdClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basip
{
    class DB
    {
        private FbConnection con;
        public FbConnection DBconnect(String connect) {
            return con= new FbConnection(connect);
        }

        //12.03.2025 Получить список панелей bas-ip: id_dev, IP адрес, логин, пароль.
        public DataTable GetDevice()
        {
            string sql = $@"select d.id_dev, bp.intvalue as IP,
                bp4.strvalue as LOGIN,
                bp5.strvalue as PASS
                from device d
                join bas_param bp on d.id_dev=bp.id_dev
                left join bas_param bp4 on bp4.id_dev=d.id_dev  and bp4.param='LOGIN'
                left join bas_param bp5 on bp5.id_dev=d.id_dev  and bp5.param='PASS'
                where bp.param='IP'";

            FbCommand getcomand = new FbCommand(sql, con);

            var reader =  getcomand.ExecuteReader();
            DataTable table = new DataTable();
            table.Load(reader);
            return table;
        }

        //12.03.2025 Получить список панелей
        public DataTable GetCardForLoad(int id_dev)
        {
            string sql = $@"";

            FbCommand getcomand = new FbCommand(sql, con);

            var reader =  getcomand.ExecuteReader();
            DataTable table = new DataTable();
            table.Load(reader);
            return table;
        }

        //12.03.2025 сохранение параметров в таблицу bas_param
        public void saveParam(int id_dev,string param_name, int? data_int, string data_string) {

            string sql = $@"delete from bas_param bp where bp.id_dev={id_dev} and bp.param='{param_name}'";
            FbCommand getcomand = new FbCommand(sql, con);
            getcomand.ExecuteNonQuery();
            string data_int_ = (data_int == null) ? "NULL" : data_int.ToString();
            sql = $@"INSERT INTO BAS_PARAM (ID_DEV, PARAM, INTVALUE, STRVALUE) VALUES ({id_dev},'{param_name}',{data_int_},'{data_string}')";
            getcomand = new FbCommand(sql, con);
            getcomand.ExecuteNonQuery();
        }

        /* 12.03.2025 для всех карт для указанной панели добавить load_result как ошибка.
         * @input id_dev - id панели
         * @input messErr - сообщение, которое надо вписать в load_result
         * 
         */
        public void updateCaridxErrAll(int id_dev, string messErr) {

            string sql = $@"delete from bas_param bp where bp.id_dev={id_dev} and bp.param='{param_name}'";
            FbCommand getcomand = new FbCommand(sql, con);
            getcomand.ExecuteNonQuery();
            string data_int_ = (data_int == null) ? "NULL" : data_int.ToString();
            sql = $@"INSERT INTO BAS_PARAM (ID_DEV, PARAM, INTVALUE, STRVALUE) VALUES ({id_dev},'{param_name}',{data_int_},'{data_string}')";
            getcomand = new FbCommand(sql, con);
            getcomand.ExecuteNonQuery();
        }



    }
}
