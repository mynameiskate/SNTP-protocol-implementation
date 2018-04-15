using System;

namespace ConsoleNtpClient
{
    class Program
    {
        static void Main(string[] args)
        {
            string[] serverList = {"time.windows.com", "pool.ntp.org", "time-a.nist.gov"};
            var client = new NtpClient(serverList);
            client.GetCurrentTime();
            client.SetTime();
            Console.ReadLine();
        }
    }
}
