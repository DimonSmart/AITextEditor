using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        try
        {
            var url = "https://dimonsmartnsk.duckdns.org:8043/";
            Console.WriteLine($"Testing URL: {url}");
            var client = new HttpClient();
            client.BaseAddress = new Uri(url);
            
            Console.WriteLine($"BaseAddress: {client.BaseAddress}");
            Console.WriteLine($"Host: {client.BaseAddress.Host}");
            Console.WriteLine($"Port: {client.BaseAddress.Port}");

            // Try to connect
            var response = await client.GetAsync("api/tags");
            Console.WriteLine($"Response: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
        }
    }
}
