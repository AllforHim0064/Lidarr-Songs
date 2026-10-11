FROM python:3.12-slim
WORKDIR /app
COPY server.py reset_password.py ./
COPY static ./static
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
EXPOSE 8787
CMD ["python", "server.py"]
