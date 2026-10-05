# syntax=docker/dockerfile:1
#
# Two images from one build; run them together with docker-compose.yml.
#
#   docker build --target api -t laya-sample-api .
#   docker build --target renderer -t laya-sample-renderer .                                   # PP-OCRv5 Latin OCR
#   docker build --target renderer -t laya-sample-renderer --build-arg OCR_MODEL=PPOCRv6Medium .  # multilingual, ~4x slower
#
# api       Classification, Office conversion, Laya feedback analysis. Never parses PDFs or images with native code.
# renderer  PDFium, ImageMagick and OCR: the only process that does. Run it isolated (see docker-compose.yml).
#
# All models are baked into the images, so containers never download at startup and run offline.

# DocumentAnalysis:OcrModel value: PPOCRv5Latin (bundled in the NuGet), PPOCRv6Tiny, PPOCRv6Small or PPOCRv6Medium.
ARG OCR_MODEL=PPOCRv5Latin

# ---- Models: pinned revisions, SHA-256 verified. Downloads land in a BuildKit cache that outlives layer rebuilds,
# so editing the script or switching OCR_MODEL only fetches files that are missing or changed. Separate stages, so
# building one image never downloads the other's models.
FROM alpine:3.22 AS model-tools
RUN apk add --no-cache curl
COPY scripts/download-models.sh /download-models.sh

FROM model-tools AS laya-model
RUN --mount=type=cache,target=/cache sh /download-models.sh laya /cache/laya && mkdir -p /models && cp -r /cache/laya /models/laya

FROM model-tools AS ocr-models
ARG OCR_MODEL
# One cache folder per model, so the image only carries the chosen one.
RUN --mount=type=cache,target=/cache sh /download-models.sh ocr "$OCR_MODEL" "/cache/ocr/$OCR_MODEL" \
 && mkdir -p /models/ocr && if [ -d "/cache/ocr/$OCR_MODEL/v6" ]; then cp -r "/cache/ocr/$OCR_MODEL/v6" /models/ocr/; fi

# ---- Build: runs natively on the build machine and cross-publishes for the target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/LayaSample.Rendering/LayaSample.Rendering.csproj src/LayaSample.Rendering/packages.lock.json src/LayaSample.Rendering/
COPY src/LayaSample.Renderer/LayaSample.Renderer.csproj src/LayaSample.Renderer/packages.lock.json src/LayaSample.Renderer/
COPY src/LayaSample.Api/LayaSample.Api.csproj src/LayaSample.Api/packages.lock.json src/LayaSample.Api/
RUN dotnet restore src/LayaSample.Api/LayaSample.Api.csproj -a $TARGETARCH \
 && dotnet restore src/LayaSample.Renderer/LayaSample.Renderer.csproj -a $TARGETARCH
COPY src/ src/
# A runtime-specific publish keeps only this platform's native libraries (ImageMagick, PDFium, Skia, ONNX Runtime).
RUN dotnet publish src/LayaSample.Renderer/LayaSample.Renderer.csproj -c Release -a $TARGETARCH --no-restore -o /app/renderer \
 && dotnet publish src/LayaSample.Api/LayaSample.Api.csproj -c Release -a $TARGETARCH --no-restore -o /app/api \
 # The API renders in-process only in development; production requires the renderer, so it carries no OCR models.
 && rm -rf /app/api/models

# ---- Renderer
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS renderer
WORKDIR /app
COPY --from=build /app/renderer .
# Merges models/v6 (when chosen) next to the models/v5 set the NuGet published.
COPY --from=ocr-models /models/ocr/ ./models/
ARG OCR_MODEL
ENV DocumentAnalysis__OcrModel=$OCR_MODEL
EXPOSE 8080
USER $APP_UID
# Ready once the OCR models are loaded. The image has no curl, so bash's /dev/tcp makes the request.
HEALTHCHECK --interval=30s --timeout=5s --start-period=1m --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "LayaSample.Renderer.dll"]

# ---- API (last, so it is also the default target)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /app/api .
COPY --from=laya-model /models/laya /models/laya
# DocumentAnalysis__RendererUrl must be set at run time (docker-compose.yml does); the API refuses to start without it.
ENV Laya__ModelPath=/models/laya
EXPOSE 8080
USER $APP_UID
# Ready once Laya is loaded; Degraded (still 200) when Laya failed for good or the renderer is down, since the rest of
# the service works.
HEALTHCHECK --interval=30s --timeout=5s --start-period=2m --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "LayaSample.Api.dll"]
