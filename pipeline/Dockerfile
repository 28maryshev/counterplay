# Коллектор: сбор матч-статистики + публикация базы. Тонкий образ — сбор
# I/O-bound (ждём Riot API), тяжёлых зависимостей нет.
FROM python:3.12-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

# Небуферизованный stdout — иначе docker logs молчит часами (сбор долгий).
ENV PYTHONUNBUFFERED=1
CMD ["python", "collector_service.py"]
