namespace Ruoyu.Study.Vocabulary.Domain;

public static class BookCategories
{
    public const int PrimarySchool = 1;
    public const int JuniorHigh = 2;
    public const int SeniorHigh = 3;
    public const int CET4 = 4;
    public const int CET6 = 5;
    public const int Professional = 6;
    public const int Postgraduate = 7;
    public const int BusinessEnglish = 8;
    public const int DailyConversation = 9;
    public const int TravelEnglish = 10;
    public const int KidsEnglish = 11;

    public static bool IsValidCategory(int? category)
    {
        return category is >= PrimarySchool and <= KidsEnglish;
    }

    public static string GetName(int category)
    {
        return category switch
        {
            PrimarySchool => "小学英语",
            JuniorHigh => "初中英语",
            SeniorHigh => "高中英语",
            CET4 => "大学四级",
            CET6 => "大学六级",
            Professional => "专业英语",
            Postgraduate => "研究生英语",
            BusinessEnglish => "商务英语",
            DailyConversation => "日常口语",
            TravelEnglish => "旅行英语",
            KidsEnglish => "儿童英语",
            _ => "未知分类"
        };
    }
}