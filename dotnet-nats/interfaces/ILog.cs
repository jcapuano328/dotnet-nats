﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dotnet_nats
{
    public interface ILog
    {
        string Level { get; set; }
        void Log(string level, string msg, Exception ex = null, params object[] args);
        void Trace(string msg, params object[] args);        
        void Debug(string msg, params object[] args);        
        void Info(string msg, params object[] args);        
        void Warn(string msg, params object[] args);                
        void Error(string msg, params object[] args);
        void Error(string msg, Exception ex, params object[] args);        
        void Fatal(string msg, params object[] args);
    }
}
