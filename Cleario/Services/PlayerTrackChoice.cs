namespace Cleario.Services
{
    public sealed class PlayerTrackChoice
    {
        public int Id { get; }
        public string Label { get; }

        public PlayerTrackChoice(int id, string label)
        {
            Id = id;
            Label = string.IsNullOrWhiteSpace(label) ? id.ToString() : label;
        }
    }
}
