# syntax=docker/dockerfile:1
#
# Four images from one build; run them together with docker-compose.yml.
#
#   docker build --target api -t docintake-api .
#   docker build --target extraction -t docintake-extraction .
#   docker build --target decisions -t docintake-decisions .
#   docker build --target renderer -t docintake-renderer .                                    # PP-OCRv5 Latin OCR
#   docker build --target renderer -t docintake-renderer --build-arg OCR_MODEL=PPOCRv6Medium .  # multilingual, ~4x slower
#
# api       The gateway: auth, scanning, risk policy, usage. It reads no document itself and runs no model, so it carries no PDF,
#           Office, image, OCR or ONNX library, holds no model provider key, and starts in seconds.
# extraction  Turns a prepared document into structured data with the model provider. The only process that holds the
#           provider's key and reaches the internet; it never sees the caller or the file's name.
# renderer  The document service (the target keeps its old name until the services are deployed one by one): classifies
#           and prepares every upload with PdfPig, the Office libraries, PDFium, ImageMagick and OCR. The only process
#           that reads untrusted bytes. Run it isolated (see docker-compose.yml).
#
# decisions Laya, the local model that scores hotel-guest feedback. Carries the 850 MB model, needs no network, takes minutes
#           to become ready.
#
# All models are baked into the images, so containers never download at startup and run offline.

# DocumentAnalysis:OcrModel value: PPOCRv5Latin (bundled in the NuGet), PPOCRv6Tiny, PPOCRv6Small or PPOCRv6Medium.
ARG OCR_MODEL=PPOCRv5Latin

# ---- Models: pinned revisions, SHA-256 verified. A copy already in the local models/ folder (where `dotnet run` and
# scripts/download-models.sh put them) is used when its hash matches, so curl only runs for files that are missing
# there. Downloads land in a BuildKit cache that outlives layer rebuilds. Separate stages, so building one image never
# needs the other's models.
FROM alpine:3.22 AS model-tools
RUN apk add --no-cache curl
COPY scripts/download-models.sh /download-models.sh
# A Windows checkout may have CRLF line endings, which sh cannot run; .gitattributes prevents that for new checkouts.
RUN sed -i 's/\r$//' /download-models.sh

FROM model-tools AS laya-model
RUN --mount=type=cache,target=/cache --mount=type=bind,source=models,target=/local \
    MODEL_SEED=/local/elbruno_laya-onnx sh /download-models.sh laya /cache/laya \
 && mkdir -p /models && cp -r /cache/laya /models/laya

FROM model-tools AS ocr-models
ARG OCR_MODEL
# One cache folder per model, so the image only carries the chosen one.
RUN --mount=type=cache,target=/cache --mount=type=bind,source=models,target=/local \
    MODEL_SEED=/local/ocr/v6 sh /download-models.sh ocr "$OCR_MODEL" "/cache/ocr/$OCR_MODEL" \
 && mkdir -p /models/ocr && if [ -d "/cache/ocr/$OCR_MODEL/v6" ]; then cp -r "/cache/ocr/$OCR_MODEL/v6" /models/ocr/; fi

# ---- Build: runs natively on the build machine and cross-publishes for the target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/DocIntake.Contracts/DocIntake.Contracts.csproj src/DocIntake.Contracts/packages.lock.json src/DocIntake.Contracts/
COPY src/DocIntake.ServiceDefaults/DocIntake.ServiceDefaults.csproj src/DocIntake.ServiceDefaults/packages.lock.json src/DocIntake.ServiceDefaults/
COPY src/DocIntake.Rendering/DocIntake.Rendering.csproj src/DocIntake.Rendering/packages.lock.json src/DocIntake.Rendering/
COPY src/DocIntake.DocumentService/DocIntake.DocumentService.csproj src/DocIntake.DocumentService/packages.lock.json src/DocIntake.DocumentService/
COPY src/DocIntake.ExtractionService/DocIntake.ExtractionService.csproj src/DocIntake.ExtractionService/packages.lock.json src/DocIntake.ExtractionService/
COPY src/DocIntake.DecisionsService/DocIntake.DecisionsService.csproj src/DocIntake.DecisionsService/packages.lock.json src/DocIntake.DecisionsService/
COPY src/DocIntake.Api/DocIntake.Api.csproj src/DocIntake.Api/packages.lock.json src/DocIntake.Api/
RUN dotnet restore src/DocIntake.Api/DocIntake.Api.csproj -a $TARGETARCH \
 && dotnet restore src/DocIntake.DocumentService/DocIntake.DocumentService.csproj -a $TARGETARCH \
 && dotnet restore src/DocIntake.ExtractionService/DocIntake.ExtractionService.csproj -a $TARGETARCH \
 && dotnet restore src/DocIntake.DecisionsService/DocIntake.DecisionsService.csproj -a $TARGETARCH
COPY src/ src/
# A runtime-specific publish keeps only this platform's native libraries (ImageMagick, PDFium, Skia, ONNX Runtime).
RUN dotnet publish src/DocIntake.DocumentService/DocIntake.DocumentService.csproj -c Release -a $TARGETARCH --no-restore -o /app/document \
 && dotnet publish src/DocIntake.ExtractionService/DocIntake.ExtractionService.csproj -c Release -a $TARGETARCH --no-restore -o /app/extraction \
 && dotnet publish src/DocIntake.DecisionsService/DocIntake.DecisionsService.csproj -c Release -a $TARGETARCH --no-restore -o /app/decisions \
 && dotnet publish src/DocIntake.Api/DocIntake.Api.csproj -c Release -a $TARGETARCH --no-restore -o /app/api

# ---- Document service
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS renderer
WORKDIR /app
COPY --from=build /app/document .
# Merges models/v6 (when chosen) next to the models/v5 set the NuGet published.
COPY --from=ocr-models /models/ocr/ ./models/
ARG OCR_MODEL
ENV DocumentAnalysis__OcrModel=$OCR_MODEL
EXPOSE 8080
USER $APP_UID
# Ready once the OCR models are loaded. The image has no curl, so bash's /dev/tcp makes the request.
HEALTHCHECK --interval=30s --timeout=5s --start-period=1m --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "DocIntake.DocumentService.dll"]

# ---- Extraction service
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS extraction
WORKDIR /app
COPY --from=build /app/extraction .
# Extraction__ApiKey (or OPENAI_API_KEY) must be set at run time; without it the service runs and answers 503.
EXPOSE 8080
USER $APP_UID
# Ready once it is running; it reports Healthy with or without a key.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "DocIntake.ExtractionService.dll"]

# ---- Decisions service
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS decisions
WORKDIR /app
COPY --from=build /app/decisions .
COPY --from=laya-model /models/laya /models/laya
ENV Laya__ModelPath=/models/laya
EXPOSE 8080
USER $APP_UID
# Ready once Laya is loaded (a few minutes on a small node); Degraded (still 200) when the load failed for good.
HEALTHCHECK --interval=30s --timeout=5s --start-period=3m --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "DocIntake.DecisionsService.dll"]

# ---- API (last, so it is also the default target)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /app/api .
# DocumentAnalysis__DocumentServiceUrl must be set at run time (docker-compose.yml does); the API refuses to start without it.
# Where the usage history (SQLite) is kept; a volume mounted here starts out writable for the non-root user.
RUN mkdir /data && chown "$APP_UID" /data
EXPOSE 8080
USER $APP_UID
# Ready at once (it loads no model); Degraded (still 200) when the document, extraction or decisions service is down, since
# the rest of the service works.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "DocIntake.Api.dll"]
