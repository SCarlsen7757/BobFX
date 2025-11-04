namespace BobFx.Core.Services
{
    public class CountdownOptions
    {
        public TimeSpan PreCountdownDuration { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan CountdownDuration { get; set; } = TimeSpan.FromSeconds(600);
        public TimeSpan CountdownDeviation { get; set; } = TimeSpan.FromSeconds(120);
    }
}
