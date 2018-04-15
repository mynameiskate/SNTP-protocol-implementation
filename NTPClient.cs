using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleNtpClient
{
    /// Structure of the standard NTP header (as described in RFC 2030)
    ///                       1                   2                   3
    ///   0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |LI | VN  |Mode |    Stratum    |     Poll      |   Precision   |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |                          Root Delay                           |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |                       Root Dispersion                         |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |                     Reference Identifier                      |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |                                                               |
    ///  |                   Reference Timestamp (64)                    |
    ///  |                                                               |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |                                                               |
    ///  |                   Originate Timestamp (64)                    |
    ///  |                                                               |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |                                                               |
    ///  |                    Receive Timestamp (64)                     |
    ///  |                                                               |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |                                                               |
    ///  |                    Transmit Timestamp (64)                    |
    ///  |                                                               |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |                 Key Identifier (optional) (32)                |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |                                                               |
    ///  |                                                               |
    ///  |                 Message Digest (optional) (128)               |
    ///  |                                                               |
    ///  |                                                               |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// 
    /// -----------------------------------------------------------------------------
    /// 
    /// SNTP Timestamp Format (as described in RFC 2030)
    ///                         1                   2                   3
    ///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// |                           Seconds                             |
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// |                  Seconds Fraction (0-padded)                  |
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// 
    class NtpClient
    {
        private const int SntpHeaderLength = 0x30;
        private const int ReferenceTimestampOffset = 0x10;
        private const int OriginateTimestampOffset = 0x18;
        private const int ReceiveTimestampOffset = 0x20;
        private const int TransmitTimestampOffset = 0x28;

        private const int sntpDefaultPort = 123;
        private string[] _serverList;
        private byte[] sntpTable = new byte[SntpHeaderLength];

        public NtpClient(string[] listOfServers)
        {
            _serverList = listOfServers;
        }

        private bool IsValid(byte[] sntpData)
        {
            return sntpData.Length >= SntpHeaderLength;
        }


        public void GetCurrentTime()
        {
            sntpTable = ConnectToServers();
            if (sntpTable != null)
            {
                Console.WriteLine("Current time: {0} ", DateTime.Now.AddMilliseconds(LocalClockOffset));
            }
            else
            {
                Console.WriteLine("Connection error.");
            }
        }

        private byte[] GetSntpHeader(string hostname)
        {
            try
            {
                var sntpTable = new byte[SntpHeaderLength];
                //Setting Leap indicator to no warning, Version number to IPv4 only and Mode to client
                sntpTable[0] = 0x1B;
                // Initialize the transmit timestamp

                SetDate(TransmitTimestampOffset, DateTime.Now, ref sntpTable);

                IPAddress[] addressArr = Dns.GetHostEntry(hostname).AddressList;
                var serverEP = new IPEndPoint(addressArr[0], sntpDefaultPort);
                var udpSocket = new UdpClient();
                udpSocket.Connect(serverEP);
                udpSocket.Send(sntpTable, sntpTable.Length);
                sntpTable = udpSocket.Receive(ref serverEP);
                if (!IsValid(sntpTable))
                {
                    throw new Exception("Invalid response from " + hostname);
                }
                DestinationTimestamp = DateTime.Now;
                return sntpTable;
            }
            catch 
            {
                return null;
            }
        }

        public static Task<T> FirstSuccessfulTask<T>(IEnumerable<Task<T>> tasks)
        {
            var taskList = tasks.ToList();
            var tcs = new TaskCompletionSource<T>();
            int remainingTasks = taskList.Count;
            foreach (var task in taskList)
            {
                task.ContinueWith(t =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                        tcs.TrySetResult(t.Result);
                    else
                    if (Interlocked.Decrement(ref remainingTasks) == 0)
                        tcs.SetException(new AggregateException(
                            tasks.SelectMany(subTask => subTask.Exception.InnerExceptions)));
                });
                return tcs.Task;
            }
            return null;
        }

        public byte[] ConnectToServers()
        {
            try
            {
                var requests = new Task<byte[]>[_serverList.Length];
                int i = 0;
                foreach (string server in _serverList)
                {
                    requests[i++] = Task.Factory.StartNew(() => GetSntpHeader(server));
                }

                return FirstSuccessfulTask(requests).Result;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }
        }  

        private ulong ToMilliseconds(int tableOffset)
        {
            // Get the seconds part
            ulong intPart = BitConverter.ToUInt32(sntpTable, tableOffset);

            // Get the seconds fraction
            ulong fractPart = BitConverter.ToUInt32(sntpTable, tableOffset + 0x4);

            if (BitConverter.IsLittleEndian)
            {
                //Convert From little-endian to big-endian
                intPart = SwapEndianness(intPart);
                fractPart = SwapEndianness(fractPart);
            }

            ulong milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);
            return milliseconds;
        }

        static uint SwapEndianness(ulong x)
        {
            return (uint)(((x & 0x000000ff) << 0x18) |
                           ((x & 0x0000ff00) << 0x8) |
                           ((x & 0x00ff0000) >> 0x8) |
                           ((x & 0xff000000) >> 0x18));
        }

        private DateTime DestinationTimestamp { get; set; }

        // Originate Timestamp (T1)
        public DateTime OriginateTimestamp
        {
            get
            {
                return ComputeDate(ToMilliseconds(OriginateTimestampOffset));
            }
        }

        // Receive Timestamp (T2)
        public DateTime ReceiveTimestamp
        {
            get
            {
                DateTime time = ComputeDate(ToMilliseconds(ReceiveTimestampOffset));
                TimeSpan offspan = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
                return time + offspan;
            }
        }

        // Transmit Timestamp (T3)
        public DateTime TransmitTimestamp
        {
            get
            {
                DateTime time = ComputeDate(ToMilliseconds(TransmitTimestampOffset));
                TimeSpan offspan = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
                return time + offspan;
            }
        }

        private void SetDate(byte offset, DateTime date, ref byte[] sntpTable)
        {
            ulong intpart = 0, fractpart = 0;
            DateTime StartOfCentury = new DateTime(1900, 1, 1, 0, 0, 0);   

            ulong milliseconds = (ulong)(date - StartOfCentury).TotalMilliseconds;
            intpart = milliseconds / 1000;
            fractpart = ((milliseconds % 1000) * 0x100000000L) / 1000;

            ulong temp = intpart;
            for (int i = 3; i >= 0; i--)
            {
                sntpTable[offset + i] = (byte)(temp % 256);
                temp = temp / 256;
            }

            temp = fractpart;
            for (int i = 7; i >= 4; i--)
            {
                sntpTable[offset + i] = (byte)(temp % 256);
                temp = temp / 256;
            }
        }

        private DateTime ComputeDate(ulong milliseconds)
        {
            TimeSpan span = TimeSpan.FromMilliseconds(milliseconds);
            DateTime time = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            time += span;
            return time;
        }

        // Local clock offset (in milliseconds)
        public int LocalClockOffset
        {
            get
            {
                TimeSpan span = (ReceiveTimestamp - OriginateTimestamp) + (TransmitTimestamp - DestinationTimestamp);
                return (int)(span.TotalMilliseconds / 2);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME
        {
            public ushort wYear;
            public ushort wMonth;
            public ushort wDayOfWeek;
            public ushort wDay;
            public ushort wHour;
            public ushort wMinute;
            public ushort wSecond;
            public ushort wMilliseconds;
        }

        [DllImport("kernel32.dll", EntryPoint = "SetSystemTime", SetLastError = true)]
        public extern static bool Win32SetSystemTime(ref SYSTEMTIME sysTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetSystemTime(ref SYSTEMTIME time);


        // Set system time according to transmit timestamp
        public void SetTime()
        {
            SYSTEMTIME st;
            DateTime newTime = DateTime.Now.AddMilliseconds(LocalClockOffset);

            st.wYear = (ushort)newTime.Year;
            st.wMonth = (ushort)newTime.Month;
            st.wDayOfWeek = (ushort)newTime.DayOfWeek;
            st.wDay = (ushort)newTime.Day;
            st.wHour = (ushort)newTime.Hour;
            st.wMinute = (ushort)newTime.Minute;
            st.wSecond = (ushort)newTime.Second;
            st.wMilliseconds = (ushort)newTime.Millisecond;

            if (!Win32SetSystemTime(ref st))
                Console.WriteLine( Marshal.GetLastWin32Error());
           
        }
    }
}
