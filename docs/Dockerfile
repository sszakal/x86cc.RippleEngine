# Builds the static site identically locally and in CI.
# Usage: docker build -t x86cc-rippleengine-docs . && docker create --name extract x86cc-rippleengine-docs && docker cp extract:/app/build ./build && docker rm extract
FROM node:24-slim

WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build
