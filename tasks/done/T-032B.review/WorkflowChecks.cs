using Iris.Core.Settings;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Iris.Core.Common;
using Iris.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

// Bind this file after B's product DLL is available. This is not a passing result.
static class WorkflowChecks
{
    private static void Check(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);}

    internal static async Task Run(ReviewSta sta)
    {
        await Scenario(sta,"joint commit and external clean value",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();
                f.Vm.LogLevel="Warning";
                bool copyTarget=!f.Settings.Current.Ocr.AutoCopyOnOcr;
                bool magnifierTarget=!f.Settings.Current.Screenshot.ShowMagnifier;
                f.Vm.Ocr.AutoCopyOnOcr.Value=copyTarget;
                f.Vm.Screenshot.MaskOpacityText.Value="0.9";
                f.Settings.ExternalChange(s=>s with{Screenshot=s.Screenshot with{ShowMagnifier=magnifierTarget}});
                await f.Vm.ExecuteSaveAsync();
                Check(f.Settings.Updates==1,"Joint plan must publish once");
                var current=f.Settings.Current;
                Check(current.General.LogLevel=="Warning" && current.Ocr.AutoCopyOnOcr==copyTarget && current.Screenshot.MaskOpacity==0.9,
                    "Joint plan omitted an edited domain");
                Check(current.Screenshot.ShowMagnifier==magnifierTarget,"Save overwrote clean external field");
                Check(!f.Vm.IsDirty,"Successful commit did not advance baseline");
            });
        });
        await Scenario(sta,"invalid screenshot draft survives",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();
                foreach(var input in new[]{"", "bad", "NaN", "Infinity", "-0.1", "0.91"})
                {
                    f.Vm.Screenshot.MaskOpacityText.Value=input;
                    await f.Vm.ExecuteSaveAsync();
                    Check(f.Settings.Updates==0,"Invalid opacity committed");
                    Check(f.Vm.Screenshot.MaskOpacityText.Value==input && f.Vm.IsDirty,"Invalid text lost");
                    Check(!f.Vm.IsSaving && f.Vm.CanEdit,"Invalid draft stranded UI busy state");
                }
            });
        });
        await Scenario(sta,"cleanup retry retains original provider",async f=>
        {
            f.Local.Configure=p=>p.FailDispose=true;
            await sta.Run(async ()=>
            {
                f.Show();f.Vm.SelectedOcrProfile=f.Vm.OcrProfiles.Single(p=>p.IsWindows);
                await f.Vm.ExecuteOcrTestAsync();
                Check(f.Vm.HasFailedOcrCleanup && f.Vm.HasOcrTestOwnership,"Run success hid failed cleanup");
                var provider=f.Local.Created.Single();
                Check(provider.TestCalls==1 && provider.RecognizeCalls==0,"Draft test called wrong operation");
                int firstDisposals=provider.DisposeCalls;
                await f.Vm.RetryOcrCleanupAsync();
                Check(provider.DisposeCalls>firstDisposals && provider.TestCalls==1 && f.Local.Created.Count==1,
                    "Cleanup retry did not retry the original resource only");
                f.Vm.Screenshot.MaskOpacityText.Value="0.8";
                await f.Vm.ExecuteSaveAsync();
                Check(f.Settings.Updates==0 && f.Vm.IsDirty,"Save crossed failed cleanup");
                provider.FailDispose=false;
                await f.Vm.RetryOcrCleanupAsync();
                Check(!f.Vm.HasOcrTestOwnership && !f.Vm.HasFailedOcrCleanup,"Successful retry retained ownership");
                Check(provider.TestCalls==1 && f.Local.Created.Count==1,"Cleanup retry ran a second test");
                await f.Vm.ExecuteSaveAsync();
                Check(f.Settings.Updates==1,"Save did not recover after cleanup");
            });
        });
        await Scenario(sta,"noncooperative test blocks save",async f=>
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Releases.Add(()=>release.TrySetResult());
            f.Local.Configure=p=>p.Body=async _=>{entered.TrySetResult();await release.Task;return new(true,TimeSpan.Zero);};
            Task? run=null,save=null;
            await sta.Run(()=>
            {
                f.Show();f.Vm.SelectedOcrProfile=f.Vm.OcrProfiles.Single(p=>p.IsWindows);
                run=f.Track(f.Vm.ExecuteOcrTestAsync());return Task.CompletedTask;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await sta.Run(()=>
            {
                f.Vm.Screenshot.MaskOpacityText.Value="0.8";
                save=f.Track(f.Vm.ExecuteSaveAsync());return Task.CompletedTask;
            });
            Check(!save!.IsCompleted && f.Settings.Updates==0,"Save failed to wait for actual test exit");
            release.TrySetResult();await run!;await save;
            Check(f.Settings.Updates==1,"Save failed after actual test exit");
        });
    }

    internal static async Task Controls(ReviewSta sta)
    {
        await Scenario(sta,"password boxes preserve partial input and clear on commit",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();f.Vm.CurrentPageKey="ocr";
                var draft=f.Vm.OcrProfiles.Single(p=>p.IsBaidu);f.Vm.SelectedOcrProfile=draft;
                draft.Credentials.Value=CredentialEditKind.Replace;
                var key=(PasswordBox)f.Window.FindName("OcrApiKeyPasswordBox");
                var secret=(PasswordBox)f.Window.FindName("OcrSecretKeyPasswordBox");
                key.Password="synthetic-review-key";
                Check(key.Password=="synthetic-review-key" && draft.ReplacementApiKey==key.Password,"Password change lost current key");
                await f.Vm.ExecuteSaveAsync();
                Check(f.Settings.Updates==0 && key.Password=="synthetic-review-key","Incomplete pair saved or input lost");
                secret.Password="synthetic-review-secret";
                f.Vm.SelectedOcrProfile=f.Vm.OcrProfiles.Single(p=>p.IsWindows);
                Check(key.Password.Length==0 && secret.Password.Length==0,"Switch exposed another profile credentials");
                f.Vm.SelectedOcrProfile=draft;
                Check(key.Password=="synthetic-review-key" && secret.Password=="synthetic-review-secret","Switch discarded uncommitted credentials");
                await f.Vm.ExecuteSaveAsync();
                Check(f.Settings.Updates==1 && key.Password.Length==0 && secret.Password.Length==0,"Commit did not clear actual password controls");
                Check(f.Vm.SelectedOcrProfile!.Credentials.Value==CredentialEditKind.Keep,"Credential intent did not reset");
            });
        });
        await Scenario(sta,"save flushes explicit opacity binding",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();f.Vm.CurrentPageKey="screenshot";
                var box=(TextBox)f.Window.FindName("ScreenshotMaskOpacityTextBox");
                var prior=f.Vm.Screenshot.MaskOpacityText.Value;
                BindingOperations.SetBinding(box,TextBox.TextProperty,new Binding("Screenshot.MaskOpacityText.Value")
                    {Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.Explicit});
                box.SetCurrentValue(TextBox.TextProperty,"not-a-number");
                Check(f.Vm.Screenshot.MaskOpacityText.Value==prior,"Setup was not explicit binding");
                await f.Vm.ExecuteSaveAsync();
                Check(f.Vm.Screenshot.MaskOpacityText.Value=="not-a-number" && f.Settings.Updates==0,"Save used stale valid value");
            });
        });
        await Scenario(sta,"actual close failure preserves clickable retry",async f=>
        {
            f.Local.Configure=p=>p.FailDispose=true;
            await sta.Run(async ()=>
            {
                f.Show();f.Vm.CurrentPageKey="ocr";f.Vm.SelectedOcrProfile=f.Vm.OcrProfiles.Single(p=>p.IsWindows);
                await f.Vm.ExecuteOcrTestAsync();
                f.Window.Close();
            });
            try{await f.Session.CurrentCleanupTask!;}catch(InvalidOperationException){}
            await sta.Run(async ()=>
            {
                Check(f.Session.IsOpen && f.Window.IsVisible,"Cleanup failure destroyed original window");
                var button=(Button)f.Window.FindName("OcrRetryCleanupButton");
                f.Window.UpdateLayout();
                Check(button.IsVisible && button.IsEnabled && button.Command!.CanExecute(button.CommandParameter),"Cleanup retry not actionable");
                f.Local.Created.Single().FailDispose=false;
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                // Routed Click does not execute ButtonBase's command automatically: execute the bound command as a UI click would.
                button.Command.Execute(button.CommandParameter);
                await Task.CompletedTask;
            });
            var until=DateTime.UtcNow.AddSeconds(10);
            while(f.Session.IsOpen && DateTime.UtcNow<until)await Task.Delay(10);
            Check(!f.Session.IsOpen,"Successful cleanup retry did not close original session");
            Check(f.Local.Created.Single().TestCalls==1,"Close retry reran test");
        });
    }

    internal static async Task Receipts(ReviewSta sta)
    {
        await Scenario(sta,"OCR pending receipt retries without recommit",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();f.Observer.Fail=true;
                f.Vm.Ocr.AutoCopyOnOcr.Value=!f.Settings.Current.Ocr.AutoCopyOnOcr;
                await f.Vm.ExecuteSaveAsync();
                Check(f.Settings.Updates==1 && f.Receipts.PendingCount==1 && !f.Vm.IsDirty,"ApplyPending lost saved fact or receipt");
                f.Vm.Screenshot.MaskOpacityText.Value="0.7";
                f.Observer.Fail=false;await f.Vm.ExecuteRetryPendingApplyAsync();
                Check(f.Settings.Updates==1 && f.Receipts.PendingCount==0,"Retry applied by recommitting");
                Check(f.Vm.Screenshot.MaskOpacityText.Value=="0.7" && f.Vm.IsDirty,"Retry lost new draft");
            });
        });
        await Scenario(sta,"OCR and screenshot honor receipt capacity",async f=>
        {
            for(int i=0;i<f.Receipts.MaxCapacity;i++)Check(f.Receipts.TryReserveSlot(),"Could not reserve capacity");
            try
            {
                await sta.Run(async ()=>
                {
                    f.Vm.Ocr.AutoCopyOnOcr.Value=!f.Settings.Current.Ocr.AutoCopyOnOcr;
                    await f.Vm.ExecuteSaveAsync();
                    Check(f.Settings.Updates==0 && f.Vm.IsDirty,"OCR bypassed capacity");
                    f.Vm.ExecuteReload();f.Vm.Screenshot.MaskOpacityText.Value="0.7";
                    await f.Vm.ExecuteSaveAsync();
                    Check(f.Settings.Updates==0 && f.Vm.IsDirty,"Screenshot bypassed capacity");
                });
            }
            finally{for(int i=0;i<f.Receipts.MaxCapacity;i++)f.Receipts.ReleaseReservation();}
        });
    }

    internal static async Task Profiles(ReviewSta sta)
    {
        await Scenario(sta,"custom active profile deletion requires explicit selection",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();
                var builtIn=f.Vm.OcrProfiles.First();f.Vm.SelectedOcrProfile=builtIn;
                int original=f.Vm.OcrProfiles.Count;f.Vm.DeleteOcrProfile();
                Check(f.Vm.OcrProfiles.Count==original,"Built-in deletion allowed");
                f.Vm.AddOcrProfile(OcrProviderKind.WindowsLocal);
                var added=f.Vm.SelectedOcrProfile!;string id=added.Id;
                added.DisplayName.Value="Custom local";added.TimeoutText.Value="120";added.LanguageTag.Value="zh-CN";
                f.Vm.Ocr.ActiveProviderId.Value=id;f.Vm.Ocr.FallbackProviderId.Value=null;
                await f.Vm.ExecuteSaveAsync();
                Check(f.Settings.Updates==1 && f.Settings.Current.Ocr.Providers.Any(p=>p.Id==id && p.TimeoutSeconds==120 && p.WindowsOptions!.LanguageTag=="zh-CN"),"Custom profile not preserved");
                f.Vm.SelectedOcrProfile=f.Vm.OcrProfiles.Single(p=>p.Id==id);f.Vm.DeleteOcrProfile();
                await f.Vm.ExecuteSaveAsync();
                Check(f.Settings.Updates==1 && f.Vm.Ocr.ActiveProviderId.Value==id,"Deletion silently reassigned active provider");
                f.Vm.Ocr.ActiveProviderId.Value=builtIn.Id;
                await f.Vm.ExecuteSaveAsync();
                Check(f.Settings.Updates==2 && !f.Settings.Current.Ocr.Providers.Any(p=>p.Id==id),"Explicit deletion failed");
            });
        });
        await Scenario(sta,"dirty external conflict preserves original baseline",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();f.Vm.Screenshot.MaskOpacityText.Value="0.8";
                f.Settings.ExternalChange(s=>s with{Screenshot=s.Screenshot with{MaskOpacity=0.6}});
                await f.Vm.ExecuteSaveAsync();
                Check(f.Settings.Current.Screenshot.MaskOpacity==0.6 && f.Vm.IsConflict,"Dirty baseline drifted to external value");
                Check(f.Vm.Screenshot.MaskOpacityText.Value=="0.8" && f.Vm.IsDirty,"Conflict discarded user target");
                f.Vm.ExecuteReload();
                Check(f.Vm.Screenshot.MaskOpacityText.Value=="0.6" && !f.Vm.IsDirty,"Reload failed to rebuild baseline");
            });
        });
        await Scenario(sta,"all Baidu modes and options round trip",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();
                foreach(var mode in Enum.GetValues<BaiduOcrMode>())
                {
                    var p=f.Vm.OcrProfiles.Single(p=>p.IsBaidu);f.Vm.SelectedOcrProfile=p;
                    p.DisplayName.Value="Baidu "+mode;p.Enabled.Value=false;p.TimeoutText.Value="5";
                    p.Mode.Value=mode;p.DetectDirection.Value=true;p.Paragraph.Value=true;
                    p.LanguageType.Value="CHN_ENG";p.BaseUrl.Value="https://ocr.example.invalid";
                    await f.Vm.ExecuteSaveAsync();
                    var saved=f.Settings.Current.Ocr.Providers.Single(x=>x.Id==p.Id);
                    Check(saved.DisplayName=="Baidu "+mode && !saved.Enabled && saved.TimeoutSeconds==5,"Common profile fields lost");
                    Check(saved.BaiduOptions is {DetectDirection:true,Paragraph:true,LanguageType:"CHN_ENG",BaseUrl:"https://ocr.example.invalid"} b && b.Mode==mode,"Baidu options lost");
                }
                Check(f.Settings.Updates==4,"Modes did not commit once each");
            });
        });
    }

    internal static async Task Identity(ReviewSta sta)
    {
        await Scenario(sta,"same ID OCR cancellation does not cancel translation",async f=>
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ocrEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ocrRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Releases.Add(()=>release.TrySetResult());f.Releases.Add(()=>ocrRelease.TrySetResult());
            CancellationToken translationToken=default;
            var provider=Substitute.For<ITranslationProvider>();
            provider.TestAsync(Arg.Any<CancellationToken>()).Returns(async call=>
            {translationToken=call.Arg<CancellationToken>();entered.TrySetResult();await release.Task;return new ProviderTestResult(true,TimeSpan.Zero,"translation marker");});
            var id=f.Settings.Current.Ocr.Providers.Single(p=>p.Kind==OcrProviderKind.WindowsLocal).Id;
            var profile=new TranslationProviderProfile{Id=id,DisplayName="Shared ID",Kind=TranslationProviderKind.OpenAICompatible,Model="test",BaseUrl="http://localhost:1234/v1"};
            var snapshot=Substitute.For<ITranslationProviderSnapshot>();
            snapshot.Providers.Returns(new[]{new TranslationProviderRegistration(profile,provider,Guid.NewGuid())});
            f.Root.GetRequiredService<ITranslationProviderRegistry>().CreateTransientAsync(Arg.Any<TranslationProviderProfile>(),Arg.Any<ProviderCredentials>(),Arg.Any<CancellationToken>()).Returns(Task.FromResult(snapshot));
            f.Local.Configure=p=>p.Body=async _=>{ocrEntered.TrySetResult();await ocrRelease.Task;return new(true,TimeSpan.Zero);};
            Task? translation=null,ocr=null;
            await sta.Run(()=>
            {
                f.Settings.ExternalChange(s=>s with{Translation=s.Translation with{Providers=[profile]}});
                f.Vm.ExecuteReload();f.Show();f.Vm.SelectedProfile=f.Vm.Profiles.Single(p=>p.Id==id);
                f.Vm.SelectedProfile.CredentialEditKind=CredentialEditKind.Replace;
                f.Vm.SelectedProfile.SetReplacementApiKey("synthetic");
                translation=f.Track(f.Vm.ExecuteTestDraftAsync());return Task.CompletedTask;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await sta.Run(()=>{f.Vm.SelectedOcrProfile=f.Vm.OcrProfiles.Single(p=>p.Id==id);ocr=f.Track(f.Vm.ExecuteOcrTestAsync());return Task.CompletedTask;});
            await ocrEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await sta.Run(()=>{f.Vm.CancelOcrTest();return Task.CompletedTask;});
            Check(!translationToken.IsCancellationRequested && !translation!.IsCompleted,"OCR shared translation cancellation identity");
            ocrRelease.TrySetResult();await ocr!;release.TrySetResult();await translation!;
            await sta.Run(()=>{Check(f.Vm.SelectedProfile!.TestSuccess==true && f.Vm.SelectedProfile.TestSampleTranslation=="translation marker","Translation result lost to OCR cancellation");return Task.CompletedTask;});
        });
    }

    internal static async Task Revisions(ReviewSta sta)
    {
        await Scenario(sta,"edited input invalidates late OCR result",async f=>
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Releases.Add(()=>release.TrySetResult());
            f.Local.Configure=p=>p.Body=async _=>{entered.TrySetResult();await release.Task;return new(true,TimeSpan.Zero);};
            Task? run=null;
            await sta.Run(()=>
            {
                f.Show();f.Vm.SelectedOcrProfile=f.Vm.OcrProfiles.Single(p=>p.IsWindows);
                run=f.Track(f.Vm.ExecuteOcrTestAsync());return Task.CompletedTask;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await sta.Run(()=>{f.Vm.SelectedOcrProfile!.LanguageTag.Value="en-US";return Task.CompletedTask;});
            release.TrySetResult();await run!;
            await sta.Run(()=>
            {
                Check(f.Vm.SelectedOcrProfile!.TestMessage==null,"Old success attached to revised draft");
                Check(!f.Vm.HasOcrTestOwnership,"Canceled old draft left resource ownership");
                return Task.CompletedTask;
            });
        });
        await Scenario(sta,"OCR test validates only selected profile current text",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();f.Vm.CurrentPageKey="ocr";f.Vm.SelectedOcrProfile=f.Vm.OcrProfiles.Single(p=>p.IsWindows);
                f.Vm.SelectedOcrProfile.Enabled.Value=false;
                f.Vm.Screenshot.MaskOpacityText.Value="invalid unrelated";
                var timeout=(TextBox)f.Window.FindName("OcrTimeoutTextBox");
                BindingOperations.SetBinding(timeout,TextBox.TextProperty,new Binding("TimeoutText.Value")
                    {Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.Explicit});
                timeout.SetCurrentValue(TextBox.TextProperty,"bad timeout");
                await f.Vm.ExecuteOcrTestAsync();
                Check(f.Local.Created.IsEmpty && f.Vm.SelectedOcrProfile.TimeoutText.Value=="bad timeout","Test used previous valid timeout");
                timeout.SetCurrentValue(TextBox.TextProperty,"5");
                await f.Vm.ExecuteOcrTestAsync();
                Check(f.Local.Created.Single().TestCalls==1 && f.Settings.Updates==0,"Unrelated invalid screenshot blocked disabled draft test");
            });
        });
    }

    internal static async Task RetryBarrier(ReviewSta sta)
    {
        await Scenario(sta,"shutdown waits for cleanup retry UI completion",async f=>
        {
            f.Local.Configure=p=>p.FailDispose=true;
            await sta.Run(async ()=>
            {
                f.Show();f.Vm.SelectedOcrProfile=f.Vm.OcrProfiles.Single(p=>p.IsWindows);
                await f.Vm.ExecuteOcrTestAsync();
                Check(f.Vm.HasFailedOcrCleanup,"Cleanup failure injection did not run");
            });
            f.Local.Created.Single().FailDispose=false;
            var uiEntered=f.Ui.Pause();
            Task? retry=null,close=null;
            await sta.Run(()=>{retry=f.Track(f.Vm.RetryOcrCleanupAsync());return Task.CompletedTask;});
            await uiEntered.WaitAsync(TimeSpan.FromSeconds(10));
            await sta.Run(()=>{close=f.Track(f.Session.ShutdownAsync());return Task.CompletedTask;});
            bool closedEarly=close!.IsCompleted;
            f.Ui.Release();await retry!;await close;
            Check(!closedEarly,"Shutdown finished while original cleanup retry still awaited UI completion");
        });
    }

    internal static async Task RetrySaveBarrier(ReviewSta sta)
    {
        await Scenario(sta,"save waits for cleanup retry UI completion",async f=>
        {
            f.Local.Configure=p=>p.FailDispose=true;
            await sta.Run(async ()=>
            {
                f.Show();f.Vm.SelectedOcrProfile=f.Vm.OcrProfiles.Single(p=>p.IsWindows);
                await f.Vm.ExecuteOcrTestAsync();
                Check(f.Vm.HasFailedOcrCleanup,"Cleanup failure injection did not run");
                f.Vm.Screenshot.MaskOpacityText.Value="0.8";
            });
            f.Local.Created.Single().FailDispose=false;
            var uiEntered=f.Ui.Pause(once:true);
            Task? retry=null,save=null;
            await sta.Run(()=>{retry=f.Track(f.Vm.RetryOcrCleanupAsync());return Task.CompletedTask;});
            await uiEntered.WaitAsync(TimeSpan.FromSeconds(10));
            await sta.Run(()=>{save=f.Track(f.Vm.ExecuteSaveAsync());return Task.CompletedTask;});
            await Task.WhenAny(save!,Task.Delay(500));
            bool committedEarly=f.Settings.Updates!=0;
            f.Ui.Release();await retry!;await save!;
            Check(!committedEarly,"Save committed while original cleanup retry still awaited UI completion");
            Check(f.Settings.Updates==1,"Save did not finish after releasing cleanup UI");
        });
    }

    internal static async Task Reachability(ReviewSta sta)
    {
        await Scenario(sta,"small window OCR options and screenshot input reachable",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();f.Window.Width=600;f.Window.Height=440;
                var directory=System.IO.Path.Combine(AppContext.BaseDirectory,"renders-reachability");System.IO.Directory.CreateDirectory(directory);
                foreach(var dark in new[]{false,true})
                foreach(var name in new[]{"OcrModeCombo","OcrLanguageTypeTextBox","ScreenshotMaskOpacityTextBox"})
                {
                    f.Vm.Theme=dark?AppTheme.Dark:AppTheme.Light;
                    bool screenshot=name.StartsWith("Screenshot");f.Vm.CurrentPageKey=screenshot?"screenshot":"ocr";
                    f.Window.UpdateLayout();await f.Window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ContextIdle);
                    var scroll=(ScrollViewer)f.Window.FindName(screenshot?"ScreenshotPageScroll":"OcrPageScroll");
                    var target=(FrameworkElement)f.Window.FindName(name);
                    target.BringIntoView();f.Window.UpdateLayout();
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset+target.TransformToAncestor(scroll).Transform(new Point()).Y-30);
                    f.Window.UpdateLayout();await Task.Delay(150);
                    var bounds=target.TransformToAncestor(scroll).TransformBounds(new Rect(target.RenderSize));
                    Check(bounds.Top>=0 && bounds.Bottom<=scroll.ActualHeight && bounds.Right<=scroll.ActualWidth,"Target remains clipped after scrolling: "+name);
                    var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(600,440,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                    var background=new System.Windows.Media.DrawingVisual();
                    using(var drawing=background.RenderOpen())drawing.DrawRectangle(dark?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32,32,32)):System.Windows.Media.Brushes.WhiteSmoke,null,new Rect(0,0,600,440));
                    bitmap.Render(background);bitmap.Render(f.Window);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output=System.IO.File.Create(System.IO.Path.Combine(directory,$"{(dark?"dark":"light")}-{name}.png"));encoder.Save(output);
                }
            });
        });
    }

    internal static async Task Render(ReviewSta sta)
    {
        await Scenario(sta,"owned page renders",async f=>
        {
            await sta.Run(async ()=>
            {
                f.Show();
                var directory=System.IO.Path.Combine(AppContext.BaseDirectory,"renders-v4");
                System.IO.Directory.CreateDirectory(directory);
                foreach(var dark in new[]{false,true})
                foreach(var page in new[]{"ocr","screenshot"})
                {
                    f.Vm.Theme=dark?AppTheme.Dark:AppTheme.Light;
                    f.Vm.CurrentPageKey=page;f.Window.Width=640;f.Window.Height=600;f.Window.UpdateLayout();
                    var scroll=(ScrollViewer)f.Window.FindName(page=="ocr"?"OcrPageScroll":"ScreenshotPageScroll");
                    foreach(var bottom in new[]{false,true})
                    {
                        if(bottom)scroll.ScrollToEnd();else scroll.ScrollToTop();
                        f.Window.UpdateLayout();
                        await f.Window.Dispatcher.InvokeAsync(()=>f.Window.UpdateLayout(),System.Windows.Threading.DispatcherPriority.ContextIdle);
                        await Task.Delay(600);
                        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(f.Window.ActualWidth),(int)Math.Ceiling(f.Window.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
                        var background=new System.Windows.Media.DrawingVisual();
                        using(var drawing=background.RenderOpen())drawing.DrawRectangle(dark?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32,32,32)):System.Windows.Media.Brushes.WhiteSmoke,null,new Rect(0,0,f.Window.ActualWidth,f.Window.ActualHeight));
                        bitmap.Render(background);bitmap.Render(f.Window);
                        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        var path=System.IO.Path.Combine(directory,$"{page}-{(dark?"dark":"light")}-{(bottom?"bottom":"top")}.png");
                        using(var output=System.IO.File.Create(path))encoder.Save(output);
                        Console.WriteLine(path);
                    }
                }
            });
        });
    }

    private static async Task Scenario(ReviewSta sta,string name,Func<ReviewScope,Task> body)
    {
        var scope=await sta.Run(()=>new ReviewScope(sta));
        Exception? bodyError=null,cleanupError=null;
        try{await body(scope);}catch(Exception ex){bodyError=ex;}
        try{await scope.DisposeAsync();}catch(Exception ex){cleanupError=ex;}
        if(bodyError!=null && cleanupError!=null)throw new AggregateException(name,bodyError,cleanupError);
        if(bodyError!=null)throw new InvalidOperationException(name,bodyError);
        if(cleanupError!=null)throw new InvalidOperationException(name+" cleanup",cleanupError);
        Console.WriteLine("PASS "+name);
    }
}
