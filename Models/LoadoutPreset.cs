namespace DL_Skin_Randomiser.Models
{
    public class LoadoutPreset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public List<LoadoutPick> Picks { get; set; } = [];
        public string PickCountText => Picks.Count == 1 ? "1 pick" : $"{Picks.Count} picks";
        public string DisplayName => $"{Name} ({PickCountText})";
    }
}
