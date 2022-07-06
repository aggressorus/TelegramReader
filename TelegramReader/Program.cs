using System;
using System.Threading.Tasks;

namespace TelegramReader
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Reader reader = new((authType) =>
            {
                if (authType == AuthType.Code)
                {
                    Console.WriteLine("Enter code:");
                }
                else if (authType == AuthType.Password)
                {
                    Console.WriteLine("Enter password:");
                }
                return Console.ReadLine().Trim();
            });

            await reader.RunAsync();

            var chatId = await reader.GetChatIdAsync("test");
            var messages = reader.GetMessagesAsync(chatId);

            Console.WriteLine("Hello World!");
        }
    }
}
