using Colossal.Logging;
using Game;
using Game.Modding;

namespace GODsDisasterControl
{
    public sealed class Mod : IMod
    {
        public const string Version = "0.14.13";

        public static readonly ILog Log = LogManager
            .GetLogger("GODsDisasterControl")
            .SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info("GODs Disaster Control v" + Version + " loading");
            updateSystem.UpdateAt<Systems.DisasterTargetToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<Systems.DisasterControlUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            Log.Info("GODs Disaster Control disposed");
        }
    }
}
