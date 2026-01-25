// Test file for EditFile flexible matching
using System;
using System.Threading.Tasks;

namespace TestApp
{
    public class Calculator  // Testing EditFile functionality
    {
        public async Task<int> Add(int a, int b)
        {
            await Task.Delay(1);
            return a + b;
        }

        public int Subtract(int a, int b)
        {
            return a - b;
        }

        public void PrintResult(int result)
        {
            Console.WriteLine(result);
            Console.WriteLine(result);
            Console.WriteLine(result);
        }
    }
}
