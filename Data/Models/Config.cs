using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TranscriberBot.Data.Models
{
    public class Config
    {
        public string BotToken { get; set; }
        public string AssemblyAIToken { get; set; }
    }
}
