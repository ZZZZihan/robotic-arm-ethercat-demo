using System;

namespace ScaraArm.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                int protocol = Sc4iProtocolTests.Run();
                int client = Sc4iClientTests.Run();
                int scara = ScaraKinematicsTests.Run();
                int planner = CartesianMotionPlannerTests.Run();
                int tasks = TaskProgramTests.Run();
                int total = protocol + client + scara + planner + tasks;
                Console.WriteLine(
                    "PASS: total=" + total
                    + " (protocol=" + protocol
                    + ", client=" + client
                    + ", scara=" + scara
                    + ", planner=" + planner
                    + ", tasks=" + tasks
                    + "); synthetic data and loopback only.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}
