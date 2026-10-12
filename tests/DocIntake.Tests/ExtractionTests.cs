using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using DocIntake.Contracts;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.Api.Services.Security;
using DocIntake.Api.Services.Usage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

public class ExtractionTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static ExtractionOptions Options(Action<ExtractionOptions>? tweak = null)
    {
        var o = new ExtractionOptions { VisionModel = "vision-model", TextModel = "text-model" };
        tweak?.Invoke(o);
        return o;
    }

    private static PreparedPart Image(int page) => new(PartRole.PageImage, "image/png", page, null, Png);
    private static PreparedPart Ocr(int page, string text) => new(PartRole.OcrText, "text/plain", page, text, null);
    private static PreparedPart PageText(int page, string text) => new(PartRole.PageText, "text/markdown", page, text, null);

    private static DocumentClassification Classification(int pages) => new()
    {
        Kind = DocumentKind.ScannedPdf, Strategy = PreparationStrategy.Hybrid, Route = DocumentRoutes.Vision,
        Reason = "test", MediaType = "application/pdf", PageCount = pages
    };

    private static DocumentExtractor Extractor(IChatClient? vision, IChatClient? text, ExtractionOptions? options = null, IExtractionErrorMapper? errors = null,
        ModelPricing? pricing = null, IUsageRecorder? recorder = null, SecurityOptions? security = null)
    {
        IExtractionAgent[] agents = [new LoanApplicationAgent(), new Ucc1Agent(), new Ucc3Agent(), new SecurityAgreementAgent(), new GenericLendingAgent()];
        var metrics = new DocumentMetrics(new TestMeterFactory());
        var riskScanner = new RiskScanner(Microsoft.Extensions.Options.Options.Create(security ?? new SecurityOptions()), metrics, NullLogger<RiskScanner>.Instance);
        return new DocumentExtractor(Microsoft.Extensions.Options.Options.Create(options ?? Options()), new ExtractionClients(vision, text),
            errors ?? new OpenAiExtractionErrorMapper(), agents, metrics,
            pricing ?? new ModelPricing(Microsoft.Extensions.Options.Options.Create(new PricingOptions())), recorder ?? new NoopUsageRecorder(),
            riskScanner, NullLogger<DocumentExtractor>.Instance);
    }

    private sealed class TestMeterFactory : System.Diagnostics.Metrics.IMeterFactory
    {
        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) => new(options);
        public void Dispose() { }
    }

    private static PreparedDocument Ucc1Scan(params PreparedPart[] extra) =>
        new(PreparationStrategy.Hybrid, [Image(1), Ocr(1, "UCC FINANCING STATEMENT\nFILE 2026-0001234"), .. extra]);

    // --- message building ---

    [Fact]
    public void Pages_are_labelled_in_order_with_their_image_and_text()
    {
        var doc = new PreparedDocument(PreparationStrategy.PerPage, [Ocr(2, "second"), Image(2), Image(1), Ocr(1, "first")]);

        var request = ExtractionMessageBuilder.Build("rules", doc, Options());

        var user = request.Messages[1].Contents;
        var order = user.Select(c => c switch { TextContent t => t.Text, DataContent => "<image>", _ => "?" }).ToList();
        Assert.Equal(["<document>", "--- Page 1 ---", "<image>", "[OCR text of this page]\nfirst", "--- Page 2 ---", "<image>",
            "[OCR text of this page]\nsecond", "</document>"], order.Take(8));
        Assert.True(request.UsesImages);
        Assert.Equal("rules", request.Messages[0].Text);
    }

    [Fact]
    public void Image_detail_comes_from_options()
    {
        var request = ExtractionMessageBuilder.Build("s", Ucc1Scan(), Options(o => o.ImageDetail = "low"));

        var image = request.Messages[1].Contents.OfType<DataContent>().Single();
        Assert.Equal("low", image.AdditionalProperties!["detail"]);
        Assert.Equal("image/png", image.MediaType);
    }

    [Fact]
    public void Images_beyond_the_cap_are_dropped_with_a_warning_but_their_text_is_kept()
    {
        var doc = new PreparedDocument(PreparationStrategy.Hybrid, [Image(1), Image(2), Image(3), Ocr(3, "third page text")]);

        var request = ExtractionMessageBuilder.Build("s", doc, Options(o => o.MaxImagePages = 2));

        Assert.Equal(2, request.Messages[1].Contents.OfType<DataContent>().Count());
        Assert.Contains(request.Messages[1].Contents.OfType<TextContent>(), t => t.Text.Contains("third page text"));
        Assert.Contains("page(s) 3 were sent as text only", Assert.Single(request.Warnings));
    }

    [Fact]
    public void Text_only_documents_send_no_images()
    {
        var doc = new PreparedDocument(PreparationStrategy.Markdown, [PageText(1, "## Page 1\nhello")]);

        var request = ExtractionMessageBuilder.Build("s", doc, Options());

        Assert.False(request.UsesImages);
        Assert.Empty(request.Messages[1].Contents.OfType<DataContent>());
    }

    [Fact]
    public void Form_values_and_attachments_are_included_as_text()
    {
        var doc = new PreparedDocument(PreparationStrategy.RenderToPng,
        [
            Image(1),
            new PreparedPart(PartRole.StructuredData, "application/json", null, "{\"name\":\"Jane\"}", null),
            new PreparedPart(PartRole.StructuredData, "application/json", null, "{\"id\":7}", null, "invoice.json")
        ]);

        var text = string.Join("\n", ExtractionMessageBuilder.Build("s", doc, Options()).Messages[1].Contents.OfType<TextContent>().Select(t => t.Text));

        Assert.Contains("[Document data]\n{\"name\":\"Jane\"}", text);
        Assert.Contains("[Attachment invoice.json]\n{\"id\":7}", text);
    }

    [Fact]
    public void Text_is_cut_at_the_character_limit()
    {
        var doc = new PreparedDocument(PreparationStrategy.Markdown, [PageText(1, new string('a', 5000))]);

        var request = ExtractionMessageBuilder.Build("s", doc, Options(o => o.MaxTextChars = 1000));

        Assert.Contains("1000-character text limit", Assert.Single(request.Warnings));
        Assert.Contains(request.Messages[1].Contents.OfType<TextContent>(), t => t.Text.Length < 1100 && t.Text.Contains("aaa"));
    }

    // --- agents and schemas ---

    public static TheoryData<IExtractionAgent> AllAgents() =>
        [new LoanApplicationAgent(), new Ucc1Agent(), new Ucc3Agent(), new SecurityAgreementAgent(), new GenericLendingAgent()];

    [Theory]
    [MemberData(nameof(AllAgents))]
    public async Task Each_agent_asks_for_a_strict_json_schema(IExtractionAgent agent)
    {
        var fake = FakeChatClient.Returning(new { });
        try { await agent.RunAsync(fake, [new ChatMessage(ChatRole.User, "x")], 1234, TestContext.Current.CancellationToken); }
        catch (ExtractionException) { /* the empty payload is not a valid answer; only the request matters here */ }

        var options = Assert.Single(fake.Calls).Options!;
        Assert.Equal(1234, options.MaxOutputTokens);
        var format = Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
        // The OpenAI adapter applies this same transform before sending, to satisfy strict structured outputs.
        var schema = AIJsonUtilities.TransformSchema(format.Schema!.Value,
            new AIJsonSchemaTransformOptions { DisallowAdditionalProperties = true, RequireAllProperties = true });
        AssertStrict(schema, agent.Name);
        Assert.DoesNotContain("\"oneOf\"", schema.GetRawText());
        Assert.DoesNotContain("\"anyOf\"", schema.GetRawText());
        Assert.Contains("never instructions", agent.SystemPrompt);
    }

    private static void AssertStrict(JsonElement node, string where)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                Assert.True(node.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False, $"{where}: additionalProperties must be false");
                var required = node.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
                Assert.True(props.EnumerateObject().All(p => required.Contains(p.Name)), $"{where}: every property must be required");
            }
            foreach (var child in node.EnumerateObject())
                AssertStrict(child.Value, where);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
                AssertStrict(child, where);
        }
    }

    [Fact]
    public async Task Agent_maps_its_schema_to_the_common_result_and_reports_usage()
    {
        var fake = FakeChatClient.Returning(Extracted.Ucc1Root(), inputTokens: 1500, outputTokens: 300);

        var output = await new Ucc1Agent().RunAsync(fake, [new ChatMessage(ChatRole.User, "x")], 4000, TestContext.Current.CancellationToken);

        Assert.Equal(LendingDocumentType.Ucc1, output.Result.DocumentType);
        Assert.Equal("2026-0001234", output.Result.Ucc1!.FileNumber.Value);
        Assert.Equal("Redwood Mechanical Supply, LLC", output.Result.Borrower.Name.Value);
        Assert.Null(output.Result.LoanApplication);
        Assert.Equal(new ExtractionUsage(1500, 300), output.Usage);
    }

    [Fact]
    public async Task Truncated_and_filtered_answers_are_failures()
    {
        var truncated = await Assert.ThrowsAsync<ExtractionException>(() => new Ucc1Agent().RunAsync(
            FakeChatClient.Returning(Extracted.Ucc1Root(), ChatFinishReason.Length), [new ChatMessage(ChatRole.User, "x")], 10, TestContext.Current.CancellationToken));
        var filtered = await Assert.ThrowsAsync<ExtractionException>(() => new Ucc1Agent().RunAsync(
            FakeChatClient.Returning(Extracted.Ucc1Root(), ChatFinishReason.ContentFilter), [new ChatMessage(ChatRole.User, "x")], 10, TestContext.Current.CancellationToken));

        Assert.Equal(502, truncated.StatusCode);
        Assert.Equal(422, filtered.StatusCode);
    }

    // --- extractor ---

    [Fact]
    public async Task A_recognised_ucc1_scan_uses_the_ucc1_agent_and_the_vision_model()
    {
        var vision = FakeChatClient.Returning(Extracted.Ucc1Root());
        var text = FakeChatClient.Returning(Extracted.Ucc1Root());

        var response = await Extractor(vision, text).ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken);

        Assert.Equal(("ucc1-agent", "keywords", "vision-model", LendingDocumentType.Ucc1), (response.Agent, response.DetectedBy, response.Model, response.DocumentType));
        Assert.Single(vision.Calls);
        Assert.Empty(text.Calls);
        Assert.Contains("UCC-1", vision.Calls[0].Messages[0].Text);
    }

    [Fact]
    public async Task A_document_without_images_uses_the_text_model()
    {
        var vision = FakeChatClient.Returning(Extracted.GenericRoot(LendingDocumentType.Other));
        var text = FakeChatClient.Returning(Extracted.GenericRoot(LendingDocumentType.Other));
        var doc = new PreparedDocument(PreparationStrategy.Markdown, [PageText(1, "Some letter")]);

        var response = await Extractor(vision, text).ExtractAsync("x.docx", Classification(1), doc, TestContext.Current.CancellationToken);

        Assert.Equal(("generic-lending-agent", "model", "text-model"), (response.Agent, response.DetectedBy, response.Model));
        Assert.Empty(vision.Calls);
    }

    [Fact]
    public async Task Pages_outside_the_document_are_cleared_and_unverifiable_quotes_are_reported()
    {
        var root = Extracted.Ucc1Root();
        root.Ucc1.FileNumber.Page = 12;
        root.Ucc1.FilingDate.Evidence = "a date that is not on the page";
        var client = FakeChatClient.Returning(root);

        var response = await Extractor(client, client).ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken);

        Assert.Null(response.Extraction.Ucc1!.FileNumber.Page);
        Assert.Contains(response.Warnings, w => w.Contains("could not be found in the page text"));
    }

    [Fact]
    public async Task An_empty_type_agent_result_falls_back_to_the_generic_agent()
    {
        var blank = Extracted.Ucc1();
        var empty = new Ucc1Extraction(Extracted.Borrower(null), Extracted.Lender(null),
            blank with
            {
                FileNumber = Extracted.Empty(), FilingDate = Extracted.Empty(), FilingOffice = Extracted.Empty(),
                Jurisdiction = Extracted.Empty(), Collateral = Extracted.Empty()
            }, []);
        var calls = 0;
        var client = new FakeChatClient(() =>
        {
            var payload = calls++ == 0 ? (object)empty : Extracted.GenericRoot(LendingDocumentType.Ucc1, ucc1: Extracted.Ucc1());
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload, FakeChatClient.Json)))
            { FinishReason = ChatFinishReason.Stop, Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 } };
        });

        var response = await Extractor(client, client).ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken);

        Assert.Equal(("generic-lending-agent", "model"), (response.Agent, response.DetectedBy));
        Assert.Equal("2026-0001234", response.Extraction.Ucc1!.FileNumber.Value);
        Assert.Equal(2, client.Calls.Count);
        Assert.Equal(new ExtractionUsage(200, 20), response.Usage);
        Assert.Contains(response.Warnings, w => w.Contains("did not look like a Ucc1"));
    }

    // --- errors ---

    [Theory]
    [InlineData(429, "17", 503, 17)]
    [InlineData(429, null, 503, 30)]
    [InlineData(401, null, 503, null)]
    [InlineData(400, null, 422, null)]
    [InlineData(500, null, 502, null)]
    public async Task Provider_errors_map_to_http_statuses(int providerStatus, string? retryAfter, int expectedStatus, int? expectedRetry)
    {
        var client = FakeChatClient.Throwing(new ClientResultException(new FakeProviderResponse(providerStatus, retryAfter)));

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            Extractor(client, client).ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken));

        Assert.Equal((expectedStatus, expectedRetry), (ex.StatusCode, ex.RetryAfterSeconds));
        Assert.Equal(providerStatus == 401, ex.IsMisconfiguration);
    }

    [Fact]
    public async Task A_provider_timeout_is_a_gateway_timeout_but_a_cancelled_request_is_not_swallowed()
    {
        var timedOut = FakeChatClient.Throwing(new TaskCanceledException("network timeout"));
        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            Extractor(timedOut, timedOut).ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken));
        Assert.Equal(504, ex.StatusCode);

        using var cts = new CancellationTokenSource();
        var cancelling = new FakeChatClient(() => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Extractor(cancelling, cancelling).ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), cts.Token));
    }

    [Fact]
    public void Options_without_an_api_key_leave_extraction_unconfigured()
    {
        var saved = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Assert.False(ExtractionClientFactory.Create(Options()).IsConfigured);

            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test");
            Assert.True(ExtractionClientFactory.Create(Options()).IsConfigured);
            Assert.True(ExtractionClientFactory.Create(Options(o => o.ApiKey = "sk-explicit")).IsConfigured);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", saved);
        }
    }

    // --- UCC boxes and other fields ---

    [Fact]
    public async Task Ucc1_boxes_and_checkboxes_come_back_as_typed_attributes()
    {
        var client = FakeChatClient.Returning(Extracted.Ucc1Root());

        var response = await Extractor(client, client).ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken);

        var ucc1 = response.Extraction.Ucc1!;
        Assert.Equal("Limited liability company", ucc1.DebtorEntityType.Value);
        Assert.Equal("SOS-412876", ucc1.DebtorOrganizationId.Value);
        Assert.Null(ucc1.AdditionalDebtorName.Value);
        Assert.Equal("None", ucc1.AlternativeDesignation.Value);
        Assert.False(ucc1.RealEstateRecords.Checked);
        Assert.True(ucc1.SearchReport.AllDebtors.Checked);
        Assert.Equal("Cascade / Loan CA-2026-0117", ucc1.FilerReference.Value);
        Assert.Equal("dwhitfield@cascadeharbor.example", ucc1.ContactEmail.Value);
    }

    [Fact]
    public async Task A_ucc3_with_only_its_core_boxes_filled_does_not_fall_back_to_the_generic_agent()
    {
        var client = FakeChatClient.Returning(new Ucc3Extraction(Extracted.Borrower(), Extracted.Lender(), Extracted.Ucc3(), []));
        var doc = new PreparedDocument(PreparationStrategy.Hybrid, [Image(1), Ocr(1, "UCC FINANCING STATEMENT AMENDMENT")]);

        var response = await Extractor(client, client).ExtractAsync("x.pdf", Classification(1), doc, TestContext.Current.CancellationToken);

        Assert.Equal("ucc3-agent", response.Agent);
        Assert.Single(client.Calls);
        Assert.True(response.Extraction.Ucc3!.Termination.Checked);
        Assert.Null(response.Extraction.Ucc3.AssigneeName.Value);
    }

    [Fact]
    public async Task Other_fields_are_cleaned_and_returned_as_name_value_pairs()
    {
        NameValue[] other =
        [
            new("County", "Multnomah", 1),
            new("", "orphan value", 1),
            new("Blank value", "  ", 1),
            new("Clerk initials", "JR", 7)
        ];
        var client = FakeChatClient.Returning(Extracted.Ucc1Root(other: other));

        var response = await Extractor(client, client).ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken);

        Assert.Equal([("County", "Multnomah", (int?)1), ("Clerk initials", "JR", null)],
            response.Extraction.OtherFields.Select(o => (o.Name, o.Value, o.Page)));
    }

    [Fact]
    public async Task Checkbox_pages_outside_the_document_are_cleared()
    {
        var root = Extracted.Ucc1Root();
        root.Ucc1.RealEstateRecords.Page = 4;
        var client = FakeChatClient.Returning(root);

        var response = await Extractor(client, client).ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken);

        Assert.Null(response.Extraction.Ucc1!.RealEstateRecords.Page);
    }

    [Fact]
    public void The_ucc_prompts_name_every_box()
    {
        Assert.Contains("search report", new Ucc1Agent().SystemPrompt);
        Assert.Contains("alternative designation", new Ucc1Agent().SystemPrompt);
        Assert.Contains("assignee", new Ucc3Agent().SystemPrompt);
        Assert.Contains("authorizing", new Ucc3Agent().SystemPrompt);
        Assert.Contains("whom the change affects", new Ucc3Agent().SystemPrompt);
        Assert.Contains("filer reference data (10)", new Ucc3Agent().SystemPrompt);
        Assert.Contains("OtherFields", new LoanApplicationAgent().SystemPrompt);
        Assert.Contains("Checkboxes", new Ucc1Agent().SystemPrompt);
    }

    // --- usage and cost ---

    private sealed class CollectingRecorder : IUsageRecorder
    {
        public List<UsageRecord> Records { get; } = [];
        public void Record(UsageRecord record) => Records.Add(record);
    }

    private static readonly UsageContext Context = new(new ApiCaller("abc123def456", false), "loan.pdf", 1234, "ScannedPdf", 1, "trace-1");

    private static ModelPricing Pricing(params string[] models) => new(Microsoft.Extensions.Options.Options.Create(new PricingOptions
    {
        AsOf = "2026-10-11",
        Models = models.ToDictionary(m => m, _ => new ModelPriceOptions
        {
            Tiers = [new PriceTier { MaxInputTokens = 272_000, InputPerMillion = 2.00m, CachedInputPerMillion = 0.10m, OutputPerMillion = 10.00m }]
        }, StringComparer.OrdinalIgnoreCase)
    }));

    [Fact]
    public async Task A_model_call_is_recorded_with_its_tokens_images_and_list_price()
    {
        var recorder = new CollectingRecorder();
        var client = FakeChatClient.Returning(Extracted.Ucc1Root(), inputTokens: 10_000, outputTokens: 1_000, cachedTokens: 2_000, reasoningTokens: 300);

        var response = await Extractor(client, client, pricing: Pricing("vision-model"), recorder: recorder)
            .ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken, Context);

        // (10,000 - 2,000) x $2 + 2,000 x $0.10 + 1,000 x $10, per million tokens.
        Assert.Equal(new ExtractionUsage(10_000, 1_000, 2_000, 300, 0.0262m), response.Usage);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(("abc123def456", "loan.pdf", 1234L, "ScannedPdf", 1, 1), (record.ApiKeyFingerprint, record.FileName, record.FileBytes, record.DocumentKind, record.Pages, record.Images));
        Assert.Equal(("vision-model", 1, "ok", "trace-1"), (record.Model, record.Attempt, record.Outcome, record.TraceId));
        Assert.Equal((10_000L, 2_000L, 300L, 1_000L), (record.InputTokens, record.CachedInputTokens, record.ReasoningTokens, record.OutputTokens));
        Assert.Equal((0.0262m, "vision-model@2026-10-11"), (record.CostUsd, record.PriceKey));
    }

    [Fact]
    public async Task The_generic_retry_is_its_own_record_and_the_costs_add_up()
    {
        var blank = Extracted.Ucc1();
        var empty = new Ucc1Extraction(Extracted.Borrower(null), Extracted.Lender(null),
            blank with { FileNumber = Extracted.Empty(), FilingDate = Extracted.Empty(), FilingOffice = Extracted.Empty(), Jurisdiction = Extracted.Empty(), Collateral = Extracted.Empty() }, []);
        var calls = 0;
        var client = new FakeChatClient(() =>
        {
            var payload = calls++ == 0 ? (object)empty : Extracted.GenericRoot(LendingDocumentType.Ucc1, ucc1: Extracted.Ucc1());
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload, FakeChatClient.Json)))
            { FinishReason = ChatFinishReason.Stop, Usage = new UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 0 } };
        });
        var recorder = new CollectingRecorder();

        var response = await Extractor(client, client, pricing: Pricing("vision-model"), recorder: recorder)
            .ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken, Context);

        Assert.Equal([1, 2], recorder.Records.Select(r => r.Attempt));
        Assert.Equal(["ucc1-agent", "generic-lending-agent"], recorder.Records.Select(r => r.Agent));
        // 1,000,000 tokens is past the 272,000 tier limit; with no higher tier configured the last one still applies.
        Assert.Equal([2.00m, 2.00m], recorder.Records.Select(r => r.CostUsd!.Value));
        Assert.Equal(4.00m, response.Usage.CostUsd);
    }

    [Fact]
    public async Task A_cut_off_answer_is_recorded_with_the_tokens_it_was_billed_for()
    {
        var client = FakeChatClient.Returning(Extracted.Ucc1Root(), finish: ChatFinishReason.Length, inputTokens: 5_000, outputTokens: 4_000);
        var recorder = new CollectingRecorder();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() => Extractor(client, client, pricing: Pricing("vision-model"), recorder: recorder)
            .ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken, Context));

        Assert.Equal(502, ex.StatusCode);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(("http_502", 5_000L, 4_000L), (record.Outcome, record.InputTokens, record.OutputTokens));
        Assert.Equal(0.05m, record.CostUsd);
    }

    [Fact]
    public async Task A_provider_error_is_recorded_without_tokens_or_cost()
    {
        var client = FakeChatClient.Throwing(new ClientResultException(new FakeProviderResponse(429, null)));
        var recorder = new CollectingRecorder();

        await Assert.ThrowsAsync<ExtractionException>(() => Extractor(client, client, pricing: Pricing("vision-model"), recorder: recorder)
            .ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken, Context));

        var record = Assert.Single(recorder.Records);
        Assert.Equal("http_503", record.Outcome);
        Assert.Null(record.InputTokens);
        Assert.Null(record.CostUsd);
    }

    [Fact]
    public async Task A_model_without_a_price_is_recorded_with_an_unknown_cost()
    {
        var client = FakeChatClient.Returning(Extracted.Ucc1Root());
        var recorder = new CollectingRecorder();

        var response = await Extractor(client, client, pricing: Pricing("some-other-model"), recorder: recorder)
            .ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken, Context);

        Assert.Null(response.Usage.CostUsd);
        var record = Assert.Single(recorder.Records);
        Assert.Equal(1000L, record.InputTokens);
        Assert.Null(record.CostUsd);
        Assert.Null(record.PriceKey);
    }

    [Fact]
    public async Task Without_a_usage_context_nothing_is_recorded()
    {
        var client = FakeChatClient.Returning(Extracted.Ucc1Root());
        var recorder = new CollectingRecorder();

        await Extractor(client, client, recorder: recorder).ExtractAsync("x.pdf", Classification(1), Ucc1Scan(), TestContext.Current.CancellationToken);

        Assert.Empty(recorder.Records);
    }
}
