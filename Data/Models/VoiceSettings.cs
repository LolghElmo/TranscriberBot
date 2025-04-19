using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TranscriberBot.Data.Models
{
    public class VoiceModuleConfig
    {
        public List<ulong> TtsIgnore { get; set; } = new();
        public List<ulong> TranscriberIgnore { get; set; } = new();
    }
}
