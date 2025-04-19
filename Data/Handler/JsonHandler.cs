using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TranscriberBot.Data.Handler
{
    public static class JsonHandler
    {
        public static T LoadJson<T>(string filepath) where T : class
        {
            if (!File.Exists(filepath))
                throw new FileNotFoundException($"Configuration file '{filepath}' not found.");

            var json = File.ReadAllText(filepath);
            T data = JsonConvert.DeserializeObject<T>(json);
            return data;
        }

        public static void SaveJson<T>(string filepath, T data) where T : class
        {
            var directory = Path.GetDirectoryName(filepath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(filepath, json);
        }
    }
}
