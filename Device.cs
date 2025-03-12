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
        
        public int id_dev { get; set; }
        public string id_ctrl {  get; set; }
        public string base_url { get; set; }
        public Device(DataRow row)
        {
            try {
                byte[] ip_byte = BitConverter.GetBytes((int)row["ip"]);
                Array.Reverse(ip_byte);
                ip = new IPAddress(ip_byte);
                base_url = "http://" + ip.ToString()+":80";
               // base_url = "http://192.168.8.102:8888"; 
                id_dev = (int)row["id_dev"];
                id_ctrl = row["id_ctrl"].ToString();
            }
            catch(Exception e)
            {
                
            }
        }
        public async Task<DeviceInfo> GetInfo(int time_wait) 
        {
            string uri = "api/info";
            RestClient restClient=new RestClient(new RestClientOptions { Timeout = TimeSpan.FromSeconds(time_wait), BaseUrl=new Uri(base_url) });
            var get = await restClient.ExecuteGetAsync(new RestRequest(uri));
            if (get == null || get.Content==null)
            return null;
            return JsonSerializer.Deserialize<DeviceInfo>(get.Content);
        }
    }
}
