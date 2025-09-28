using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;

public struct DnsResult
{
    public DnsResult(int ip_iter, string ip, string hostname)
    {
        this.ip_iter = ip_iter;
        this.ip = ip;
        this.hostname = hostname;
    }
    public int ip_iter;
    public string ip;
    public string hostname;
}

public struct HttpResult
{
    public HttpResult(DnsResult dns_result, HttpResponseMessage response)
    {
        this.dns_result = dns_result;
        this.response = response;
    }
    public DnsResult dns_result;
    public HttpResponseMessage response;
}

namespace ScanTheWorld
{
    class Program
    {
        static void Main(string[] args)
        {
            // Print version from the VERSION file
            string version = File.ReadAllText("VERSION").Trim();
            Console.WriteLine("NetCartographer v{0}", version);

            // Load ip lists
            string[] ip_list = File.ReadAllLines("ip_list.txt");
            int ip_iter = 0;
            Console.WriteLine("Loaded {0:n0} IP addresses from ip_list.txt", ip_list.Length);

            // Create tasks list
            int http_total_tasks = 100;
            var http_tasks = new List<Task<HttpResult>>();
            int dns_total_tasks = 100;
            var dns_tasks = new List<Task<DnsResult>>();

            // Create handler to ignore SSL certificate errors
            SocketsHttpHandler http_handler = new SocketsHttpHandler
            {
                // ignore SSL certificate errors
                SslOptions = { RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true }

            };

            // Create HTTP client
            HttpClient client = new HttpClient(http_handler);

            // Set user agent and timeout
            client.Timeout = TimeSpan.FromSeconds(5); // Set a timeout for requests
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"NetCartographer/{version}");

            while (true)
            {
                // Create DNS tasks
                while (dns_tasks.Count < dns_total_tasks && ip_iter < ip_list.Length)
                {
                    string ip = ip_list[ip_iter];
                    dns_tasks.Add(Task.Run(() =>
                    {
                        DnsResult output = new DnsResult(ip_iter, ip, "");
                        try
                        {
                            output.hostname = Dns.GetHostEntry(ip).HostName;
                            return output;
                        }
                        catch (SocketException)
                        {
                            return output;
                        }
                    }));
                    ip_iter++;
                }

                // Create HTTPS tasks from DNS task results
                if (http_tasks.Count < http_total_tasks && dns_tasks.Count > 0)
                {
                    for (int i = 0; i < dns_tasks.Count; i++)
                    {
                        Task<DnsResult> dns_task = dns_tasks[i];
                        if (!dns_task.IsCompleted) continue;

                        // Save the dns task result
                        DnsResult dns_result = dns_task.Result;

                        // Decide whether or not to use hostname or ip
                        string target = dns_result.ip;
                        if (!string.IsNullOrEmpty(dns_result.hostname))
                        {
                            target = dns_result.hostname;
                        }

                        // Create the HTTP task
                            http_tasks.Add(Task.Run(() =>
                        {
                            HttpResult output = new HttpResult(dns_result, null);

                            // Attempt HTTP request
                            try
                            {
                                Task<HttpResponseMessage> http_response_task = client.GetAsync($"http://{dns_result.ip}/");
                                http_response_task.Wait();
                                // If we get a 301 Moved Permanently, try HTTPS
                                if (http_response_task.Result.StatusCode == HttpStatusCode.MovedPermanently)
                                {
                                    http_response_task.Dispose();
                                    Task<HttpResponseMessage> https_response_task = client.GetAsync($"https://{target}/");
                                    https_response_task.Wait();
                                    output.response = https_response_task.Result;
                                    Console.WriteLine("[{0}] HTTPS {1} {2}", dns_result.ip_iter, (int)output.response.StatusCode, output.response.ReasonPhrase);
                                    return output;
                                }
                                output.response = http_response_task.Result;
                                Console.WriteLine("[{0}] HTTP {1} {2}", dns_result.ip_iter, (int)output.response.StatusCode, output.response.ReasonPhrase);
                                return output;
                            }
                            catch (OperationCanceledException ex)
                            {
                                Console.WriteLine("[{0}] Timed out when querying for {1}, error: {2}", dns_result.ip_iter, target, ex.Message);
                                return output;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[{0}] Error when querying for {1}, error: {2}", dns_result.ip_iter, target, ex.Message);
                                return output;
                            }
                        }));
                        dns_tasks.Remove(dns_task);
                    }
                }

                // Remove completed HTTP tasks
                for (int i = 0; i < http_tasks.Count; i++)
                {
                    Task<HttpResult> http_task = http_tasks[i];
                    if (!http_task.IsCompleted) continue;
                    http_tasks.Remove(http_task);
                }
            }
        }
    }
}