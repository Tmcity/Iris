using System.Runtime.ExceptionServices;
using Iris.App.Services;
using Iris.Platform.Windows.Tests.Selection;

namespace Iris.App.Tests.Selection;

internal static class SelectionCoordinatorTestScope
{
    internal static Task Run(Func<SelectionTestScope, SelectionReadCoordinator, Task> body, SelectionTestScope? existing = null) =>
        SelectionTestScope.Run(async scope =>
        {
            var coordinator = new SelectionReadCoordinator(scope.Reader);
            Exception? primary = null;
            var errors = new List<Exception>();
            try { await body(scope, coordinator); }
            catch (Exception ex) { primary = ex; }
            finally
            {
                foreach (var gate in scope.Gates) gate.Set();
                foreach (var release in scope.Release) try { release(); } catch (Exception ex) { errors.Add(ex); }
                try { await coordinator.StopAsync(); } catch (Exception ex) { errors.Add(ex); }
                try { await coordinator.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            }
            if (primary != null && errors.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
            if (primary != null) errors.Insert(0, primary);
            if (errors.Count != 0) throw new AggregateException(errors);
        }, existing);

}
