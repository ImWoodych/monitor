/**
 * System Resource Monitor (Real-Time)
 * 
 * Выводит нагрузку на ЦП и ОЗУ в реальном времени.
 * Интервал обновления задаётся через параметры запуска.
 * 
 * Использование:
 *   monitor.exe                    # интервал по умолчанию: 1 секунда
 *   monitor.exe --interval 5       # обновление каждые 5 секунд
 *   monitor.exe -i 3               # обновление каждые 3 секунды
 * 
 * Зависимости: Node.js (для разработки), pkg (для компиляции в .exe)
 * Платформа: Windows
 */

const { exec } = require('child_process');
const os = require('os');
const util = require('util');

// Конвертируем callback-based exec в promise-based для async/await
const execAsync = util.promisify(exec);

// ============================================================
// Разбор аргументов командной строки
// ============================================================

// Интервал обновления по умолчанию (в секундах)
let interval = 1;
const args = process.argv.slice(2);

// Ищем параметры --interval или -i
for (let i = 0; i < args.length; i++) {
    if ((args[i] === '--interval' || args[i] === '-i') && i + 1 < args.length) {
        const val = parseFloat(args[i + 1]);
        if (!isNaN(val) && val > 0) {
            interval = val;
            i++;
        }
    }
}

// ============================================================
// Утилиты терминала
// ============================================================

/**
 * Перемещает курсор в левый верхний угол экрана без очистки.
 * Использует ANSI-код \x1B[H для позиционирования.
 */
function homeCursor() {
    process.stdout.write('\x1B[H');
}

// ============================================================
// Сбор данных о системе через PowerShell
// ============================================================

/**
 * Получает процент использования CPU.
 * Использует PowerShell CIM для получения LoadPercentage.
 * @returns {number|null} Процент использования CPU или null при ошибке
 */
async function getCpuUsage() {
    try {
        const { stdout } = await execAsync('powershell -Command "Get-CimInstance Win32_Processor | Select-Object -ExpandProperty LoadPercentage"');
        const match = stdout.trim().match(/(\d+)/);
        return match ? parseInt(match[1], 10) : null;
    } catch {
        return null;
    }
}

/**
 * Получает данные об использовании оперативной памяти.
 * Использует PowerShell CIM для получения FreePhysicalMemory и TotalVisibleMemorySize.
 * Скрипт передаётся через base64-кодирование для избежания проблем с экранированием.
 * @returns {object|null} Объект { usedGB, totalGB, percent } или null при ошибке
 */
async function getRamUsage() {
    // PowerShell-скрипт для расчёта использования RAM
    const psScript = `
$total = (Get-CimInstance Win32_OperatingSystem).TotalVisibleMemorySize
$free = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory
$used = $total - $free
$usedGB = [math]::Round($used / 1MB, 2)
$totalGB = [math]::Round($total / 1MB, 2)
$percent = [math]::Round(($used / $total) * 100, 1)
Write-Output "$usedGB $totalGB $percent"
`;
    try {
        // Кодируем скрипт в base64 (UTF-16LE) для передачи через -EncodedCommand
        const bytes = Buffer.from(psScript, 'utf16le');
        const encoded = bytes.toString('base64');
        const { stdout } = await execAsync('powershell -EncodedCommand ' + encoded);
        const parts = stdout.trim().split(/\s+/);
        if (parts.length >= 3) {
            return { usedGB: parts[0], totalGB: parts[1], percent: parts[2] };
        }
        return null;
    } catch {
        return null;
    }
}

// ============================================================
// Визуализация
// ============================================================

/**
 * Рисует прогресс-бар заданной ширины.
 * @param {number} percent - Процент заполнения (0-100)
 * @param {number} width - Ширина бара в символах
 * @returns {string} Строка прогресс-бара
 */
function progressBar(percent, width = 20) {
    const filled = Math.round((percent / 100) * width);
    const empty = width - filled;
    const bar = '\u2588'.repeat(filled) + '-'.repeat(empty);
    return '[' + bar + ']';
}

/**
 * Формирует строку полного отображения монитора.
 * @param {number} tick - Номер тика (обновления)
 * @param {number|null} cpu - Процент использования CPU
 * @param {object|null} ram - Данные об использовании RAM
 * @returns {string} Строка для вывода в терминал
 */
function buildDisplay(tick, cpu, ram) {
    let lines = [];
    
    // Заголовок с информацией о системе
    lines.push('========================================');
    lines.push('     System Monitor (Real-Time)');
    lines.push('========================================');
    lines.push('  Update interval : ' + interval + ' sec | Tick: ' + tick);
    lines.push('  Hostname        : ' + os.hostname());
    lines.push('  OS              : ' + os.type() + ' ' + os.release());
    lines.push('  CPUs            : ' + os.cpus().length + ' (' + os.cpus()[0].model + ')');
    lines.push('========================================');
    lines.push('');

    // Отображение нагрузки на CPU
    const cpuStr = cpu !== null ? cpu + '%' : 'N/A';
    const cpuBar = cpu !== null ? progressBar(cpu, 20) : '--------------------';
    lines.push('  CPU Usage:  ' + cpuStr + '  ' + cpuBar);

    // Отображение использования RAM
    if (ram !== null) {
        const ramStr = ram.percent + '%';
        const ramBar = progressBar(parseFloat(ram.percent), 20);
        lines.push('  RAM Usage:  ' + ramStr + '  ' + ramBar);
        lines.push('  Used:       ' + ram.usedGB + ' GB / ' + ram.totalGB + ' GB');
    } else {
        lines.push('  RAM Usage:  N/A');
    }

    lines.push('');
    lines.push('  Press Ctrl+C to exit.');
    lines.push('========================================');
    return lines.join('\n');
}

// ============================================================
// Основной цикл мониторинга
// ============================================================

async function main() {
    let tick = 0;

    // Очищаем экран при запуске
    process.stdout.write('\x1B[2J\x1B[0f');

    // Асинхронный цикл обновления
    const loop = async () => {
        tick++;

        // Получаем данные о CPU и RAM параллельно
        const [cpu, ram] = await Promise.all([getCpuUsage(), getRamUsage()]);

        // Перемещаем курсор в начало и перезаписываем весь блок
        homeCursor();
        process.stdout.write(buildDisplay(tick, cpu, ram) + '\n');

        // Планируем следующее обновление
        setTimeout(loop, interval * 1000);
    };

    // Запускаем цикл
    loop();
}

// Обработка ошибок
main().catch((err) => {
    console.error('Error:', err);
    process.exit(1);
});
