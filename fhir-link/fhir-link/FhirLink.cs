using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Task = System.Threading.Tasks.Task;

namespace fhirlink;

public class FhirLink
{
    private readonly CloudBlobClient _blogStorageClient;
    private readonly FhirClient _fhirClient;

    public FhirLink(CloudBlobClient blogStorageClient, FhirClient fhirClient)
    {
        _blogStorageClient = blogStorageClient;
        _fhirClient = fhirClient;
    }

    [FunctionName("BuildMergedPatientCsv")]
    public async Task RunAsync([TimerTrigger("0 0 */4 * * *", RunOnStartup = true)] TimerInfo timer, ILogger log)
    {
        try
        {
            log.LogInformation($"Run started at {DateTime.UtcNow}");

            var q = new SearchParams().Where("link:missing=false").Select("id", "link"); 

            log.LogInformation("Querying FHIR for patients with links");

            // CRV: The cast here will get rid of non-patients (ideally would be better to filter with the SearchParams)
            var find = await _fhirClient.SearchAsync<Patient>(q);

            var patients = find.Entry.Select(ec => ec.Resource as Patient).ToList();

            // need to find correct structure for composite key of the ids or just unique list...?
            var patientPairs = new Dictionary<(string, string), string>();

            // We are getting 5 patients with inconsistent links (only the first and last are paired, all the rest are only replaced-by and have inconsistent ID formats
            /*
                D000000001
                    Replaces WDT0000000016
                "D000000001-1"
                    Replaced by D000000001
                "4be15074-a29b-45b0-a0f9-ebd8157266a9"
                    Replaced by "a75e08fd-cf79-4396-934c-4b427e71156c"
                "2fab9a03-c932-4a05-a2ab-343193f72d9c"
                    Replaced by 12345
                WDT0000000016
                    Replaced by WDT000000001
                */


            // organize patients into unique patient pairs using dictionary
            foreach (var patient in patients)
            {
                // only get replaces links
                foreach (var link in patient.Link.Where(l => l.Type == Patient.LinkType.ReplacedBy))
                {
                    var linkedPatientId = link.Other.Reference.Split('/').LastOrDefault();

                    patientPairs.Add((patient.Id, linkedPatientId), null);
                }
            }

            log.LogInformation("Building CSV of merged patients");



            var container = _blogStorageClient.GetContainerReference("test");
            await container.CreateIfNotExistsAsync();

            // todo: clean any illegal characters and settle on filename format
            var blobName = $"merged_patients_{DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")}.csv";

            var blob = container.GetBlockBlobReference(blobName);

            using CloudBlobStream stream = await blob.OpenWriteAsync();

            stream.Write(Encoding.Default.GetBytes("Entity1,Entity1Key,Entity2,Entity2Key\n"));

            foreach (var patientPair in patientPairs)
            {
                var lineStr = $"AzureAPIforFHIR_Patient,{patientPair.Key.Item1},AzureAPIforFHIR_Patient,{patientPair.Key.Item2}\n";

                stream.Write(Encoding.Default.GetBytes(lineStr));
            }

            stream.Flush();
            stream.Close();

            log.LogInformation($"Uploading {blobName} to data lake.");
        }
        catch (Exception exception)
        {
            log.LogError(exception.Message);
        }
        finally
        {
            log.LogInformation($"Run completed at {DateTime.UtcNow}");

            log.LogInformation($"Next run scheduled at {timer.Schedule.GetNextOccurrence}");
        }
    }
}
