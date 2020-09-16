using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Search;
using Microsoft.Azure.Search.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Rest.Azure;


namespace MargiesTravel
{
    class Program
    {
        static IConfigurationBuilder builder = new ConfigurationBuilder().AddJsonFile("appsettings.json");
        static IConfigurationRoot configuration = builder.Build();

        static async Task Main(string[] args)
        {
            // First, connect to a search service
            SearchServiceClient searchService = CreateSearchServiceClient(configuration);

            string indexName = "reviewsindex";

            if (!searchService.Indexes.Exists(indexName))
            {
                // Next, create the search index
                Console.WriteLine("Deleting index...\n");
                await DeleteIndexIfExists(indexName, searchService);

                // Create the skills
                Console.WriteLine("Creating the skills....");
                
                var skills = CreateSkills();
                Skillset skillSet = CreateOrUpdateDemoSkillSet(searchService, skills);

                Console.WriteLine("Creating index...\n");
                await CreateIndex(indexName, searchService);

                // Set up a Blob Storage data source and indexer, and run the indexer to merge hotel room data
                Console.WriteLine("Indexing and merging review data from blob storage...\n");
                await CreateAndRunBlobIndexer(indexName, searchService, skillSet);

                System.Threading.Thread.Sleep(4000);
                Console.WriteLine("Complete.\n");
            }
            else
            {
                Console.WriteLine("The index already exists.  Enter 1 to delete the index.  Enter 2 to query the existing index");
                int choice = Convert.ToInt32(Console.ReadLine());
                if (choice == 1)
                {
                    await DeleteIndexIfExists(indexName, searchService);
                    Console.WriteLine("Index deleted.  Exiting app");
                }
                else if (choice == 2)
                {
                    Console.WriteLine("{0}", "Searching index...\n");
                    ISearchIndexClient indexClient = searchService.Indexes.GetClient(indexName);
                    RunQueries(indexClient);
                }
                else
                {
                    Console.WriteLine("Invalid Choice.  Exiting app");
                    Console.ReadKey();
                }
            }
            
            Console.WriteLine("Press any key to exit");
            Console.ReadKey();
        }

        private static List<Skill> CreateSkills()
        {
            LanguageDetectionSkill languageDetectionSkill = CreateLanguageDetectionSkill();

            List<Skill> skills = new List<Skill>();
            skills.Add(languageDetectionSkill);

            return skills;
        }

        private static LanguageDetectionSkill CreateLanguageDetectionSkill()
        {
            List<InputFieldMappingEntry> inputMappings = new List<InputFieldMappingEntry>();
            inputMappings.Add(new InputFieldMappingEntry(
                name: "text",
                source: "/document/content"));

            List<OutputFieldMappingEntry> outputMappings = new List<OutputFieldMappingEntry>();
            outputMappings.Add(new OutputFieldMappingEntry(
                name: "languageCode",
                targetName: "languageCode"));

            LanguageDetectionSkill languageDetectionSkill = new LanguageDetectionSkill(
                description: "Detect the language used in the document",
                context: "/document",
                inputs: inputMappings,
                outputs: outputMappings);

            return languageDetectionSkill;
        }

        private static Skillset CreateOrUpdateDemoSkillSet(SearchServiceClient serviceClient, IList<Skill> skills)
        {
            CognitiveServicesByKey cognitiveServicesByKey = new CognitiveServicesByKey("835b4523d06f4615b02cbbf61c9069e4");
            Skillset skillset = new Skillset(
                name: "margiesskillset",
                description: "Margie Travel skillset",
                skills: skills, cognitiveServicesByKey);

            // Create the skillset in your search service.
            // The skillset does not need to be deleted if it was already created
            // since we are using the CreateOrUpdate method
            try
            {
                serviceClient.Skillsets.CreateOrUpdate(skillset);
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to create the skillset\n Exception message: {0}\n", e.Message);
                //ExitProgram("Cannot continue without a skillset");
            }

            return skillset;
        }

        private static SearchServiceClient CreateSearchServiceClient(IConfigurationRoot configuration)
        {
            string searchServiceName = configuration["SearchServiceName"];
            string adminApiKey = configuration["SearchServiceAdminApiKey"];

            SearchServiceClient searchService = new SearchServiceClient(searchServiceName, new SearchCredentials(adminApiKey));
            return searchService;
        }

        private static async Task DeleteIndexIfExists(string indexName, SearchServiceClient searchService)
        {
            if (searchService.Indexes.Exists(indexName))
            {
                await searchService.Indexes.DeleteAsync(indexName);
            }
        }

        private static async Task CreateIndex(string indexName, SearchServiceClient searchService)
        {
            // Create a new search index structure that matches the properties of the Hotel class.
            // The Address and Room classes are referenced from the Hotel class. The FieldBuilder
            // will enumerate these to create a complex data structure for the index.
            var definition = new Microsoft.Azure.Search.Models.Index()
            {
                Name = indexName,
                Fields = FieldBuilder.BuildForType<TravelIndex>()
            };
            await searchService.Indexes.CreateAsync(definition);
        }

        private static async Task CreateAndRunBlobIndexer(string indexName, SearchServiceClient searchService, Skillset skillSet)
        {
            DataSource blobDataSource = DataSource.AzureBlobStorage(
                name: configuration["BlobStorageAccountName"],
                storageConnectionString: configuration["BlobStorageConnectionString"],
                containerName: "qnateam7container");

            // The blob data source does not need to be deleted if it already exists,
            // but the connection string might need to be updated if it has changed.
            await searchService.DataSources.CreateOrUpdateAsync(blobDataSource);

            Console.WriteLine("Creating Blob Storage indexer...\n");

            // Add a field mapping to match the Id field in the documents to 
            // the HotelId key field in the index
            List<FieldMapping> map = new List<FieldMapping> {
                new FieldMapping("metadata_storage_path", "id", FieldMappingFunction.Base64Encode()),
                new FieldMapping("metadata_storage_path", "url"),
                new FieldMapping("metadata_storage_name", "file_name"),
                new FieldMapping("content", "content"),
                new FieldMapping("metadata_storage_size", "size"),
                new FieldMapping("metadata_storage_last_modified", "last_modified")
            };

            Indexer blobIndexer = new Indexer(
                name: "hotelreviews-blob-indexer",
                dataSourceName: blobDataSource.Name,
                targetIndexName: indexName,
                fieldMappings: map,
                skillsetName: skillSet.Name,
                schedule: new IndexingSchedule(TimeSpan.FromDays(1)));

            // Reset the indexer if it already exists
            bool exists = await searchService.Indexers.ExistsAsync(blobIndexer.Name);
            if (exists)
            {
                await searchService.Indexers.ResetAsync(blobIndexer.Name);
            }
            await searchService.Indexers.CreateOrUpdateAsync(blobIndexer);

            Console.WriteLine("Running Blob Storage indexer...\n");

            try
            {
                await searchService.Indexers.RunAsync(blobIndexer.Name);
            }
            catch (CloudException e) when (e.Response.StatusCode == (HttpStatusCode)429)
            {
                Console.WriteLine("Failed to run indexer: {0}", e.Response.Content);
            }
        }

        // Add query logic and handle results
        private static void RunQueries(ISearchIndexClient indexClient)
        {
            SearchParameters parameters;
            DocumentSearchResult<TravelIndex> results;

            // Query 1 
            Console.WriteLine("Query 1: Search for term 'New York', returning the full document");
            
            parameters = new SearchParameters();
            parameters.SearchMode = SearchMode.All;
            parameters.QueryType = QueryType.Full;

            results = indexClient.Documents.Search<TravelIndex>("New York", parameters);
            WriteDocuments(results);

            // Query 2
           // Console.WriteLine("Query 2: Search on the term 'Atlanta', returning selected fields");
          //  Console.WriteLine("Returning only these fields: HotelName, Tags, Address:\n");
        //    parameters =
        //        new SearchParameters()
        //        {
        //            Select = new[] { "HotelName", "Tags", "Address" },
        //        };
       //     results = indexClient.Documents.Search<TravelIndex>("Atlanta", parameters);
       //     WriteDocuments(results);

            // Query 3
            Console.WriteLine("Query 2: Search for the terms 'London' and 'Buckingham Palace'");
            //Console.WriteLine("Return only these fields: HotelName, Description, and Tags:\n");
            parameters =
                new SearchParameters();
            //    {
           //         Select = new[] { "HotelName", "Description", "Tags" }
           //     };
            results = indexClient.Documents.Search<TravelIndex>("London, Buckingham Palace", parameters);
            WriteDocuments(results);

            // Query 4 -filtered query
        /*    Console.WriteLine("Query 4: Filter on ratings greater than 4");
            Console.WriteLine("Returning only these fields: HotelName, Rating:\n");
            parameters =
                new SearchParameters()
                {
                    Filter = "Rating gt 4",
                    Select = new[] { "HotelName", "Rating" }
                };
            results = indexClient.Documents.Search<TravelIndex>("*", parameters);
            WriteDocuments(results); */

            // Query 5 - top 2 results
         /*   Console.WriteLine("Query 5: Search on term 'boutique'");
            Console.WriteLine("Sort by rating in descending order, taking the top two results");
            Console.WriteLine("Returning only these fields: HotelId, HotelName, Category, Rating:\n");
            parameters =
                new SearchParameters()
                {
                    OrderBy = new[] { "Rating desc" },
                    Select = new[] { "HotelId", "HotelName", "Category", "Rating" },
                    Top = 2
                };
            results = indexClient.Documents.Search<TravelIndex>("boutique", parameters);
            WriteDocuments(results); */
        }

        // Handle search results, writing output to the console
        private static void WriteDocuments(DocumentSearchResult<TravelIndex> searchResults)
        {
            var resultsList = searchResults.Results;
            Console.WriteLine($"There are {resultsList.Count} documents in the results");

            foreach (SearchResult<TravelIndex> result in resultsList)
            {
                Console.WriteLine(result.Document.file_name);
            }

            Console.WriteLine();
        }
    }
}
