﻿/*  Copyright (C) 2014 Colton Manville
    This file is part of CSharpMiner.

    CSharpMiner is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    CSharpMiner is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with CSharpMiner.  If not, see <http://www.gnu.org/licenses/>.*/

using CSharpMiner;
using CSharpMiner.Helpers;
using CSharpMiner.Pools;
using CSharpMiner.Stratum;
using DeviceManager;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiningDevice
{
    [DataContract]
    public abstract class UsbMinerBase : IMiningDevice
    {
        [IgnoreDataMember]
        public int Id { get; set; }

        [DataMember(Name = "port")]
        public string UARTPort { get; set; }

        private int _cores = 1;
        [DataMember(Name = "cores")]
        public int Cores 
        {
            get
            {
                return _cores;
            }
            set
            {
                _cores = value;

                HashRate = this.GetTheoreticalHashrate() * Cores;
            }
        }

        [DataMember(Name = "timeout")]
        public int WatchdogTimeout { get; set; }

        [IgnoreDataMember]
        public int HashRate { get; protected set; }

        [IgnoreDataMember]
        public int HardwareErrors { get; protected set; }

        private System.Timers.Timer _timer = null;
        [IgnoreDataMember]
        public System.Timers.Timer WorkRequestTimer
        {
            get 
            {
                if (_timer == null)
                    _timer = new System.Timers.Timer();

                return _timer;
            }
        }

        [IgnoreDataMember]
        public int Accepted { get; set; }

        [IgnoreDataMember]
        public int Rejected { get; set; }

        [IgnoreDataMember]
        public int AcceptedWorkUnits { get; set; }

        [IgnoreDataMember]
        public int RejectedWorkUnits { get; set; }

        [IgnoreDataMember]
        public int DiscardedWorkUnits { get; set; }

        public event Action<IMiningDevice, IPoolWork, string> ValidNonce;
        public event Action<IMiningDevice> WorkRequested;
        public event Action<IMiningDevice, IPoolWork> InvalidNonce;

        protected Thread listenerThread = null;
        protected SerialPort usbPort = null;
        protected StratumWork pendingWork = null;

        private System.Timers.Timer watchdogTimer = null;

        private bool continueRunning = true;

        public void Load()
        {
            HashRate = GetTheoreticalHashrate();

            WorkRequestTimer.Stop();

            if(WatchdogTimeout <= 0)
            {
                WatchdogTimeout = 60; // Default to one minute if not set
            }

            watchdogTimer = new System.Timers.Timer(WatchdogTimeout * 1000);
            watchdogTimer.Elapsed += this.WatchdogExpired;
            watchdogTimer.AutoReset = true;

            if (this.listenerThread == null)
            {
                this.listenerThread = new Thread(new ThreadStart(this.Connect));
                this.listenerThread.Start();
            }
        }

        protected void RestartWorkRequestTimer()
        {
            WorkRequestTimer.Stop();
            WorkRequestTimer.Start();
        }

        private void WorkRequestTimerExpired(object sender, System.Timers.ElapsedEventArgs e)
        {
            if(this.WorkRequested != null)
            {
                this.WorkRequested(this);
            }
        }

        private void WatchdogExpired(object sender, System.Timers.ElapsedEventArgs e)
        {
            if(this.WorkRequested != null)
            {
                LogHelper.ConsoleLogErrorAsync(string.Format("Device {0} hasn't responded for {1} sec. Restarting.", this.UARTPort, (double)WatchdogTimeout / 1000));
                this.WorkRequested(this);
            }
        }
        
        protected void RestartWatchdogTimer()
        {
            if(watchdogTimer != null)
            {
                watchdogTimer.Stop();
                watchdogTimer.Start();
            }
        }

        private void Connect()
        {
            RestartWatchdogTimer();

            try
            {
                string[] portNames = SerialPort.GetPortNames();

                if (!portNames.Contains(UARTPort))
                {
                    Exception e = new SerialConnectionException(string.Format("{0} is not a valid USB port.", (UARTPort != null ? UARTPort : "null")));

                    LogHelper.LogErrorSecondary(e);

                    throw e;
                }

                try
                {
                    continueRunning = true;
                    usbPort = new SerialPort(UARTPort, GetBaud());
                    //usbPort.DataReceived += DataReceived; // This works on .NET in windows but not in Mono
                    usbPort.Open();
                }
                catch (Exception e)
                {
                    LogHelper.ConsoleLogErrorAsync(string.Format("Error connecting to {0}.", UARTPort));
                    throw new SerialConnectionException(string.Format("Error connecting to {0}: {1}", UARTPort, e), e);
                }

                LogHelper.ConsoleLogAsync(string.Format("Successfully connected to {0}.", UARTPort), LogVerbosity.Verbose);

                if (this.pendingWork != null)
                {
                    Task.Factory.StartNew(() =>
                        {
                            this.StartWork(pendingWork);
                            pendingWork = null;
                        });
                }

                while (this.continueRunning)
                {
                    if (usbPort.BytesToRead > 0)
                    {
                        DataReceived(usbPort, null);
                    }

                    Thread.Sleep(10);
                }
            }
            catch (Exception e)
            {
                LogHelper.LogErrorAsync(e);
                this.Unload();
                this.Load();
            }
        }

        protected void SubmitWork(StratumWork work, string nonce)
        {
            this.RestartWatchdogTimer();

            if(this.ValidNonce != null)
            {
                this.ValidNonce(this, work, nonce);
            }
        }

        public void Unload()
        {
            if (continueRunning)
            {
                if(this.watchdogTimer != null)
                {
                    this.watchdogTimer.Stop();
                }

                continueRunning = false;

                if (usbPort != null && usbPort.IsOpen)
                    usbPort.Close();

                if(listenerThread != null)
                    listenerThread.Join();

                listenerThread = null;
            }
        }

        protected void OnInvalidNonce(IPoolWork work)
        {
            this.InvalidNonce(this, work);
        }

        public void Dispose()
        {
            this.Unload();
        }

        public abstract void StartWork(IPoolWork work);
        public abstract int GetBaud();
        protected abstract void DataReceived(object sender, SerialDataReceivedEventArgs e);
        protected abstract int GetTheoreticalHashrate();
    }
}
