using System.Runtime.ExceptionServices;
using Iris.Core.Common;
using Iris.Core.Ocr;
using Iris.Core.Settings;
using Iris.Platform.Windows.DependencyInjection;
using Iris.Platform.Windows.Ocr;
using Microsoft.Extensions.DependencyInjection;
using static ProviderScope;

internal sealed class ReviewEnvironmentSkip(string message) : Exception(message);

internal static class SdkChecks
{
    internal static async Task Run(string directory, Func<string,Func<Task>,Task> check)
    {
        await check("ACTUAL_PLATFORM_DI_REGISTERS_LAZY_LOCAL_FACTORY", async () =>
        {
            var boundary = new OcrBoundary(); var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton<IWindowsOcrNative>(boundary); services.AddSingleton<IWindowsOcrImageFactory>(boundary);
            services.AddIrisPlatformWindows();
            await using var container = services.BuildServiceProvider();
            var factory = container.GetServices<IOcrProviderFactory>().Single(f => f.Kind == OcrProviderKind.WindowsLocal);
            Require(boundary.CapabilityReads == 0 && boundary.Engines.IsEmpty, "resolving the factory performed native work");
            var provider = factory.Create(new() { Id="di-local",DisplayName="Review",Kind=OcrProviderKind.WindowsLocal },ProviderCredentials.None);
            try { Require(provider.Descriptor.IsConfigured && boundary.CapabilityReads == 1 && boundary.Engines.IsEmpty && boundary.Images.IsEmpty, "DI-created provider exceeded capability-only initialization"); }
            finally { await ((IAsyncDisposable)provider).DisposeAsync(); }
        });
        await check("REAL_PROVIDER_RECOGNIZES_OWNED_SYNTHETIC_PNG", async () =>
        {
            var language = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("en",StringComparison.OrdinalIgnoreCase));
            if (language == null) throw new ReviewEnvironmentSkip("No installed English OCR language for owned synthetic HELLO smoke.");
            var factory = new WindowsOcrProviderFactory(new WindowsOcrNative(),new WindowsOcrImageFactory(),TimeProvider.System);
            var provider = factory.Create(new() { Id="real-local",DisplayName="Review local SDK",Kind=OcrProviderKind.WindowsLocal,TimeoutSeconds=30,
                WindowsOptions=new() { LanguageTag=language.LanguageTag } },ProviderCredentials.None);
            Exception? primary=null; var errors=new List<Exception>();
            try
            {
                var result=await provider.RecognizeAsync(new(await Input(directory),language.LanguageTag),default);
                string text=string.Join('\n',result.Lines.Select(l=>l.Text));
                string normalized=new string(text.Where(c=>!char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
                Console.WriteLine($"REAL_LOCAL_SDK language={language.LanguageTag} maximum={provider.Constraints.MaxLongSide} text={text}");
                Require(normalized=="HELLO123" && result.DetectedLanguage==null && result.Lines.All(l=>l.Confidence==null),"actual local provider did not preserve synthetic text or truthful metadata");
            }
            catch(Exception ex){primary=ex;}
            finally{try{await ((IAsyncDisposable)provider).DisposeAsync();}catch(Exception ex){errors.Add(ex);}}
            if(primary!=null&&errors.Count==0)ExceptionDispatchInfo.Capture(primary).Throw();
            if(primary!=null)errors.Insert(0,primary);
            if(errors.Count!=0)throw new AggregateException(errors);
        });
    }
}
