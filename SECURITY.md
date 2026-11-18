# Seguridad

- **No se versionan secretos**: ni API keys, ni tokens, ni contraseñas, ni archivos
  `.env`, `.pfx`, `keys.json` o credenciales.
- Las claves de IA son configuración local del usuario; la app no trae ninguna.
- Si encuentras una clave o un dato privado en el repo, abre un aviso privado en
  GitHub Security Advisories o un issue sin publicar el valor.
- El modo portable guarda los datos en `./data`; no se suben a ningún servidor.
