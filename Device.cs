using RestSharp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Basip
{
    class Device
    {
        public IPAddress ip { get; set; }
       // public int id_dev { get; set; }
       // public string id_ctrl {  get; set; }
        public string base_url { get; set; }
        public string login { get; set; }
        public string password { get; set; }
        private string hashPassword { get; set; }
        public bool is_online { get; set; } // признак связи: true - усптройство на связи, false - прибор не отвечает
        
        public Device(DataRow row)
        {
            try {
                byte[] ip_byte = BitConverter.GetBytes((int)row["ip"]);
                Array.Reverse(ip_byte);
                ip = new IPAddress(ip_byte);
                base_url = "http://" + ip.ToString()+":80";
                // base_url = "http://192.168.8.102:8888"; 
                //       id_dev = (int)row["id_dev"];
                //       id_ctrl = row["id_ctrl"].ToString();
                login = row["login"].ToString();
                password = row["password"].ToString();
            }
            catch(Exception e)
            {
                
            }
        }
        
        //получить информацию об устройстве без авторизации.
        public async Task<DeviceInfo> GetInfo(int time_wait) 
        {
            string uri = "api/info";
            RestClient restClient=new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl=new Uri(base_url) });
            var get = await restClient.ExecuteGetAsync(new RestRequest(uri));
            if (get == null || get.Content==null)
            {
                this.is_online = false;
                return null;

            }
            this.is_online = true;
            return JsonSerializer.Deserialize<DeviceInfo>(get.Content);
        }

        //попытка авторизации
        public bool Auth(string pswd) 
        {
            int a = await 1 + 1;   
        }

        //сделать хэш пароля
        // https://stackoverflow.com/questions/11454004/calculate-a-md5-hash-from-a-string
        public static string CreateMD5(string input)
        {
            // Use input string to calculate MD5 hash
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                return Convert.ToHexString(hashBytes); // .NET 5 +

                // Convert the byte array to hexadecimal string prior to .NET 5
                // StringBuilder sb = new System.Text.StringBuilder();
                // for (int i = 0; i < hashBytes.Length; i++)
                // {
                //     sb.Append(hashBytes[i].ToString("X2"));
                // }
                // return sb.ToString();
            }
        }

    }
}
