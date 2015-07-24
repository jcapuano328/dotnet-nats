﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dotnet_nats
{
    public interface ILog
    {
        void Trace(string msg);
        void Trace(string msg, params object[] args);
        void Debug(string msg);
        void Debug(string msg, params object[] args);
        void Info(string msg);
        void Info(string msg, params object[] args);
        void Warn(string msg);
        void Warn(string msg, params object[] args);
        void Error(string msg);
        void Error(string msg, params object[] args);
        void Fatal(string msg);
        void Fatal(string msg, params object[] args);
    }
}
