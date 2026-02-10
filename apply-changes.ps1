Write-Host "1. Deteniendo contenedores..."
docker-compose down

Write-Host "2. Reconstruyendo (SIN usar caché) para asegurar cambios..."
docker-compose build --no-cache web

Write-Host "3. Iniciando servidor..."
docker-compose up -d

Write-Host "¡Listo! Refresca tu navegador (Ctrl + F5)."