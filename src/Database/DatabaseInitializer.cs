using Ruoyu.Study.Common.Database;

namespace Ruoyu.Study.Vocabulary.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(VocabularyDbContext context, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
    {
        await Common.Database.DatabaseInitializer.InitializeAsync(context, loggerFactory, GetTableCreationSql);
    }

    private static string? GetTableCreationSql(string tableName)
    {
        return tableName switch
        {
            "vocabulary" => @"
                CREATE TABLE IF NOT EXISTS vocabulary (
                    id character varying(36) NOT NULL,
                    word character varying(255) NOT NULL,
                    phonetic character varying(100) NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_vocabulary PRIMARY KEY (id)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_vocabulary_word ON vocabulary (word);",

            "vocabulary_book" => @"
                CREATE TABLE IF NOT EXISTS vocabulary_book (
                    id character varying(36) NOT NULL,
                    book_name character varying(255) NOT NULL,
                    description text NULL,
                    publisher character varying(255) NULL,
                    education_level character varying(100) NULL,
                    grade character varying(50) NULL,
                    category character varying(50) NULL,
                    display_order integer NOT NULL,
                    status boolean NOT NULL,
                    icon_url text NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_vocabulary_book PRIMARY KEY (id)
                );",

            "vocabulary_meaning" => @"
                CREATE TABLE IF NOT EXISTS vocabulary_meaning (
                    id character varying(36) NOT NULL,
                    vocabulary_id character varying(36) NOT NULL,
                    book_id character varying(36) NULL,
                    part_of_speech character varying(50) NULL,
                    meaning text NOT NULL,
                    example text NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_vocabulary_meaning PRIMARY KEY (id),
                    CONSTRAINT FK_vocabulary_meaning_vocabulary_vocabulary_id 
                        FOREIGN KEY (vocabulary_id) REFERENCES vocabulary(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_vocabulary_meaning_vocabulary_id ON vocabulary_meaning (vocabulary_id);
                CREATE INDEX IF NOT EXISTS IX_vocabulary_meaning_book_id ON vocabulary_meaning (book_id);",

            _ => null
        };
    }
}
