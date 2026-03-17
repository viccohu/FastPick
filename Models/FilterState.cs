namespace FastPick.Models;

public enum FileTypeFilter
{
    All,
    Both,
    JpgOnly,
    RawOnly
}

public enum RatingFilterCondition
{
    All,
    HasRating,
    NoRating,
    Equals,
    LessOrEqual,
    GreaterOrEqual
}

public class FilterState
{
    public FileTypeFilter FileType { get; set; } = FileTypeFilter.All;
    public RatingFilterCondition RatingCondition { get; set; } = RatingFilterCondition.All;
    public int RatingValue { get; set; } = 0;
    
    public bool HasActiveFilter => FileType != FileTypeFilter.All || 
                                    RatingCondition != RatingFilterCondition.All;
    
    public void Clear()
    {
        FileType = FileTypeFilter.All;
        RatingCondition = RatingFilterCondition.All;
        RatingValue = 0;
    }
}
