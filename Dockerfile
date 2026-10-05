# syntax=docker/dockerfile:1
#
#   docker build -t laya-sample .                                       # PP-OCRv5 Latin OCR
#   docker build -t laya-sample --build-arg OCR_MODEL=PPOCRv6Medium .   # multilingual, ~4x slower per page
#   docker run --rm -p 8080:8080 laya-sample
#
# All models are baked into the image, so the container never downloads at startup and runs offline.

# DocumentAnalysis:OcrModel value: PPOCRv5Latin (bundled in the NuGet), PPOCRv6Tiny, PPOCRv6Small or PPOCRv6Medium.
ARG OCR_MODEL=PPOCRv5Latin

# ---- Models: pinned revisions, SHA-256 verified. Downloads land in a BuildKit cache that outlives layer rebuilds,
# so editing the script or switching OCR_MODEL only fetches files that are missing or changed.
FROM alpine:3.22 AS models
RUN apk add --no-cache curl
COPY scripts/download-models.sh /download-models.sh
RUN --mount=type=cache,target=/cache sh /download-models.sh laya /cache/laya && mkdir -p /models && cp -r /cache/laya /models/laya
ARG OCR_MODEL
# One cache folder per model, so the image only carries the chosen one.
RUN --mount=type=cache,target=/cache sh /download-models.sh ocr "$OCR_MODEL" "/cache/ocr/$OCR_MODEL" \
 && mkdir -p /models/ocr && if [ -d "/cache/ocr/$OCR_MODEL/v6" ]; then cp -r "/cache/ocr/$OCR_MODEL/v6" /models/ocr/; fi

# ---- Build: runs natively on the build machine and cross-publishes for the target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/LayaSample.Api/LayaSample.Api.csproj src/LayaSample.Api/
RUN dotnet restore src/LayaSample.Api/LayaSample.Api.csproj -a $TARGETARCH
COPY src/LayaSample.Api/ src/LayaSample.Api/
# A runtime-specific publish keeps only this platform's native libraries (ImageMagick, PDFium, Skia, ONNX Runtime).
RUN dotnet publish src/LayaSample.Api/LayaSample.Api.csproj -c Release -a $TARGETARCH --no-restore -o /app

# ---- Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Merges models/v6 (when chosen) next to the models/v5 set the NuGet published.
COPY --from=models /models/ocr/ ./models/
COPY --from=models /models/laya /models/laya
ARG OCR_MODEL
ENV Laya__ModelPath=/models/laya \
    DocumentAnalysis__OcrModel=$OCR_MODEL
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "LayaSample.Api.dll"]
