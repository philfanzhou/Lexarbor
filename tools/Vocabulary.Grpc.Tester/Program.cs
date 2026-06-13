using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Vocabulary.Contract.Protos;
using Ruoyu.Study.Vocabulary.Test;
using System;
using System.IO;
using System.Windows.Forms;

namespace Ruoyu.Study.Grpc.Vocabulary.Test
{
    static class Program
    {
        public static IServiceProvider? ServiceProvider { get; set; }

        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main()
        {
            try
            {
                // 配置应用程序设置
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 创建配置
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                // 创建服务容器
                var services = new ServiceCollection();

                // 添加配置
                services.AddSingleton<IConfiguration>(configuration);

                // 添加日志
                services.AddLogging(builder =>
                {
                    builder.AddConfiguration(configuration.GetSection("Logging"));
                    builder.AddConsole();
                });

                // 使用Qz.Infra.GrpcBase.Client配置gRPC客户端
                services.SetupGrpcClients(configuration);

                // 注册gRPC客户端（使用工厂模式）
                services.AddSingleton(provider =>
                {
                    var factory = provider.GetRequiredService<IGrpcClientFactory>();
                    return factory.Get<VocabularyGrpcService.VocabularyGrpcServiceClient>("vocabulary");
                });

                services.AddSingleton(provider =>
                {
                    var factory = provider.GetRequiredService<IGrpcClientFactory>();
                    return factory.Get<VocabularyBookGrpcService.VocabularyBookGrpcServiceClient>("vocabulary");
                });

                // 添加主窗体
                services.AddTransient<MainForm>();

                // 构建服务提供者
                ServiceProvider = services.BuildServiceProvider();

                // 获取主窗体并显示
                var mainForm = ServiceProvider.GetRequiredService<MainForm>();
                Console.WriteLine("MainForm instance created successfully");

                // 运行应用程序
                Application.Run(mainForm);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unhandled exception in Main: {ex.Message}");
                Console.WriteLine($"Exception Type: {ex.GetType().FullName}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                    Console.WriteLine($"Inner Exception Stack Trace: {ex.InnerException.StackTrace}");
                }
                // 等待用户按键，以便查看错误信息
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }
    }
}