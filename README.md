# APPSHOTACREEN

App de capturas de pantalla para Windows (C# · .NET 9 · WPF), inspirada en el flujo de Snagit:
capturar → anotar → copiar/guardar.

## Usar

1. Ejecuta `dist\AppShotScreen.exe`. Aparece un icono azul en la bandeja del sistema, junto al reloj.
2. Captura:
   - **Impr Pant** o **Ctrl+Shift+S**: seleccionar región (arrastra; Enter = todo; Esc = cancelar)
   - **Ctrl+Shift+F**: pantalla completa del monitor actual
   - **Ctrl+Shift+D**: captura con desplazamiento (como Snagit). Selecciona la zona; aparece un marco rojo y un panel flotante. Desplaza tú con la rueda del ratón (puedes subir y bajar) y pulsa **Detener y guardar** (o Esc). **Auto** desplaza solo hasta el final; **Cancelar** descarta.
   - Clic en el icono de la bandeja: región. Clic derecho: menú (retardo 3 s, abrir imagen, carpeta, salir)
3. La captura se copia sola al portapapeles y se abre el editor.

> Si Windows 11 tiene activado "Usar Impr Pant para abrir Recortes", ese atajo lo toma Windows;
> usa Ctrl+Shift+S o desactívalo en Configuración › Accesibilidad › Teclado.

## Editor

| Herramienta | Tecla | |
|---|---|---|
| Flecha | A | con punta rellena y sombra suave |
| Rectángulo / Elipse | R / E | |
| Resaltador | H | rectángulo semitransparente del color elegido |
| Lápiz | P | trazo libre |
| Texto | T | clic para escribir, Esc para terminar |
| Pasos numerados | S | 1, 2, 3… automáticos |
| Pixelar | B | oculta datos sensibles |
| Recortar | C | |

Ctrl+Z / Ctrl+Y deshacer y rehacer, Ctrl+C copiar, Ctrl+S guardar.
Abajo: **Sombra** y **Borde** se aplican al exportar. **Extraer texto** usa el OCR de Windows y lo copia.

Las capturas se guardan en `Imágenes\APPSHOTACREEN`. Las preferencias están en `%APPDATA%\APPSHOTACREEN\settings.json`.

## Novedades 0.2

- **Ventana de inicio** con los modos de captura y las capturas recientes (historial local en `%LOCALAPPDATA%\APPSHOTACREEN\Historial`, últimas 60). Clic en el icono de la bandeja para abrirla. Con `--tray` arranca solo en la bandeja.
- **Captura inteligente**: al pasar el ratón se resalta la ventana y un clic la captura; arrastrar sigue eligiendo una región. Lupa con coordenadas y color; **C** copia el color (#RRGGBB).
- **Presentación**: fondo degradado, margen, esquinas redondeadas, sombra y borde (panel derecho del editor). La captura se copia píxel a píxel.
- **Ocultar datos sensibles**: detecta con OCR correos, teléfonos, tarjetas, IPs e identificaciones y los pixela.
- **Fijar en pantalla** (Ctrl+Alt+P o botón Fijar): la captura flota encima; arrastrar la mueve, la rueda cambia el tamaño, doble clic la cierra y el clic derecho abre el menú.
- **Copiar texto** (Ctrl+Alt+T): selecciona una zona y su texto va al portapapeles.
- **Foco** (F): oscurece todo menos las zonas marcadas. **Clic derecho** sobre una anotación la elimina.
- Editor rediseñado: barra lateral de herramientas, barra flotante de color y grosor, tema oscuro.

Pruebas rápidas (sin las de desplazamiento): `AppShotScreen.exe --selftest salida --fast`

## Desarrollo

```bash
dotnet build src/AppShotScreen
```

Prueba automática (captura, anotaciones, efectos, guardado, portapapeles, OCR):

```bash
src/AppShotScreen/bin/Debug/net9.0-windows10.0.19041.0/AppShotScreen.exe --selftest salida
```

Publicar un único .exe:

```bash
dotnet publish src/AppShotScreen -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

### Estructura

- `Capture/` — captura de pantalla (GDI, multi-monitor y alta resolución) y ventana de selección de región
- `Editor/` — editor, anotaciones, deshacer/rehacer, exportación y efectos
- `Services/` — atajos globales, ajustes JSON y OCR (`Windows.Media.Ocr`)

### Siguientes pasos

Captura de ventana con resaltado al pasar el ratón, desplazamiento horizontal,
presets guardados (modo + selección + atajo + efectos + destino), grabación de vídeo/GIF,
más efectos (bordes rasgados, perspectiva, lupa) y temas de estilos por herramienta.
