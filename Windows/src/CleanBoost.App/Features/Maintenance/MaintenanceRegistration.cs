using CleanBoost.App.Features.Maintenance;
using CleanBoost.Core;
using CleanBoost.Features.Maintenance;

namespace CleanBoost.App.Services
{
    public sealed partial class FeatureRegistry
    {
        /// <summary>Bảo trì: feature (thẻ Smart Scan + task <c>maintenanceStatus</c>) và remover <c>maintenance</c>.</summary>
        partial void RegisterMaintenance()
        {
            var feature = new MaintenanceFeature(Env.Helper, Env.Storage);
            Add(feature);
            Add(feature.CreateRemover());
            MaintenanceRequests.Attach(App.Router);
        }
    }
}

namespace CleanBoost.App.Features.Maintenance
{
    /// <summary>
    /// Chuyển yêu cầu chạy tác vụ (<c>cleanboost://maintenance?task=…</c>, mục "Giải phóng RAM" ở khay) từ
    /// <see cref="Services.AppRouter.MaintenanceTaskRequested"/> tới màn Bảo trì. Màn chưa được tạo (app đang chạy nền ở khay)
    /// thì giữ lại yêu cầu cuối cùng, màn tạo xong sẽ nhận.
    /// </summary>
    public static class MaintenanceRequests
    {
        private static bool s_attached;
        private static MaintenanceTaskName? s_pending;
        private static Action<MaintenanceTaskName>? s_handler;

        public static void Attach(Services.AppRouter router)
        {
            if (s_attached) return;
            s_attached = true;
            router.MaintenanceTaskRequested += task =>
            {
                if (s_handler is { } handler) handler(task);
                else s_pending = task;
            };
        }

        /// <summary>Màn Bảo trì nhận yêu cầu; có yêu cầu đang chờ thì giao ngay.</summary>
        public static void SetHandler(Action<MaintenanceTaskName> handler)
        {
            s_handler = handler;
            if (s_pending is { } pending)
            {
                s_pending = null;
                handler(pending);
            }
        }
    }
}
