namespace RedoSchool.Features.LevelGeneration.Generation.Presentation.Runtime
{
    public interface IDoorAutoOpenReceiver
    {
        public void HandleDoorAutoOpen(DoorSocket door, bool isOpen);
    }
}
