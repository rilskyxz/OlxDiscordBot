using System;
using System.Threading.Tasks;

namespace OlxDiscordBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                Console.WriteLine("Inicjalizacja bota...");
                var bot = new OlxBot();
                await bot.RunAsync();
                await Task.Delay(-1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Krytyczny błąd: {ex}");
                Console.WriteLine("Naciśnij dowolny klawisz, aby zakończyć...");
                Console.ReadKey();
            }
        }


    }
}