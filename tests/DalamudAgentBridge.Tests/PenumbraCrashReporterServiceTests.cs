using DalamudAgentBridge.Plugin;
using Xunit;

namespace DalamudAgentBridge.Tests
{
    public class PenumbraCrashReporterServiceTests
    {
        [Fact]
        public void EnablePersistsAndStartsWithoutChangingPluginLifecycle()
        {
            var plugin = new Penumbra.Penumbra();
            var adapter = new PenumbraCrashReporterService(() => plugin);
            var before = adapter.Snapshot();
            Assert.True(before.Available);
            Assert.Null(before.Enabled);
            var after = adapter.SetEnabled(true, before.InstanceId!, false);
            Assert.True(after.Enabled);
            Assert.True(after.Running);
            Assert.Equal(1, plugin.Config.Advanced.Writes);
        }

        [Fact]
        public void SavedEnabledButStoppedIsReconciled()
        {
            var plugin = new Penumbra.Penumbra();
            var adapter = new PenumbraCrashReporterService(() => plugin);
            plugin.Config.Advanced.UseCrashHandler = true;
            plugin.Service.Disable();
            var before = adapter.Snapshot();
            Assert.True(adapter.SetEnabled(true, before.InstanceId!, true).Running);
        }

        [Fact]
        public void ReloadedPluginRejectsStaleReview()
        {
            var plugin = new Penumbra.Penumbra();
            var adapter = new PenumbraCrashReporterService(() => plugin);
            var before = adapter.Snapshot();
            plugin = new Penumbra.Penumbra();
            Assert.Throws<InvalidOperationException>(() => adapter.SetEnabled(true, before.InstanceId!, false));
            Assert.Equal(0, plugin.Config.Advanced.Writes);
        }

        [Fact]
        public void UnavailableIsNotReportedAsDisabled()
        {
            var adapter = new PenumbraCrashReporterService(() => throw new InvalidOperationException("Not loaded"));
            var result = adapter.Snapshot();
            Assert.False(result.Available);
            Assert.Null(result.Enabled);
            Assert.Null(result.Running);
            Assert.Equal("Not loaded", result.Error);
        }

        [Fact]
        public void DisablePersistsAndStops()
        {
            var plugin = new Penumbra.Penumbra();
            plugin.Config.Advanced.UseCrashHandler = true;
            var adapter = new PenumbraCrashReporterService(() => plugin);
            var before = adapter.Snapshot();
            var after = adapter.SetEnabled(false, before.InstanceId!, true);
            Assert.False(after.Enabled);
            Assert.False(after.Running);
        }

        [Fact]
        public void ChangedSettingRejectsStaleReview()
        {
            var plugin = new Penumbra.Penumbra();
            var adapter = new PenumbraCrashReporterService(() => plugin);
            var before = adapter.Snapshot();
            plugin.Config.Advanced.UseCrashHandler = true;
            Assert.Throws<InvalidOperationException>(() => adapter.SetEnabled(true, before.InstanceId!, false));
            Assert.True(plugin.Service.IsRunning);
        }

        [Fact]
        public void FailedStartDoesNotReportSuccess()
        {
            var plugin = new Penumbra.Penumbra();
            plugin.Service.FailStart = true;
            var adapter = new PenumbraCrashReporterService(() => plugin);
            var before = adapter.Snapshot();
            Assert.Throws<InvalidOperationException>(() => adapter.SetEnabled(true, before.InstanceId!, false));
            Assert.False(adapter.Snapshot().Running);
        }
    }
}

namespace Penumbra
{
    public class Penumbra
    {
        private readonly Configuration _config;
        private readonly ServiceManager _services;
        public Services.CrashHandlerService Service { get; } = new();
        public Configuration Config => _config;
        public Penumbra()
        {
            _config = new(Service);
            _services = new(Service);
        }
    }
    public class Configuration(Services.CrashHandlerService service)
    {
        public readonly AdvancedConfig Advanced = new(service);
    }
    public class AdvancedConfig(Services.CrashHandlerService service)
    {
        private bool? enabled;
        public int Writes;
        public bool? UseCrashHandler
        {
            get => enabled;
            set
            {
                if (enabled == value) return;
                enabled = value;
                Writes++;
                if (value == true) service.Enable(); else service.Disable();
            }
        }
    }
    public class ServiceManager(Services.CrashHandlerService service)
    {
        public T GetService<T>() where T : class => (service as T)!;
    }
}
namespace Penumbra.Services
{
    public class CrashHandlerService
    {
        public bool FailStart;
        public bool IsRunning { get; private set; }
        public int ChildProcessId => IsRunning ? 123 : -1;
        public string LogPath => "Penumbra.log";
        public void Enable() => IsRunning = !FailStart;
        public void Disable() => IsRunning = false;
    }
}
