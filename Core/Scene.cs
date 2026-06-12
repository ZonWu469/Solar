namespace Solar.Core
{
    public abstract class Scene
    {
        protected readonly GameContext Ctx;
        protected Scene(GameContext ctx) { Ctx = ctx; }
        public virtual void Enter() { }
        public virtual void Exit() { }
        public abstract void Update(double dt);
        public abstract void Draw();
    }

    public sealed class SceneManager
    {
        public Scene Current { get; private set; }
        public void SwitchTo(Scene s)
        {
            Current?.Exit();
            Current = s;
            Current.Enter();
        }
    }
}
