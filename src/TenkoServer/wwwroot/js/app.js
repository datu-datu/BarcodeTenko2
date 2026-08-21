// ==========================================================================
// TenkoServer Dashboard JavaScript
// ==========================================================================

let currentTab = 'scansTab';
let autoRefreshTimer = null;
let cachedUnverifiedEmails = [];

/**
 * XSS対策: HTML 特殊文字のエスケープ処理
 */
function escapeHtml(str) {
    if (str === null || str === undefined) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

document.addEventListener('DOMContentLoaded', async () => {
    // 認証状態チェック
    await checkAuth();

    // 日付初期設定（今日）
    const dateInput = document.getElementById('dateSelect');
    const today = new Date().toISOString().split('T')[0];
    dateInput.value = today;
    updateExportDateText(today);

    // イベントリスナー登録
    setupEventListeners();

    // 初回データ読み込み
    await refreshAllData();

    // 自動更新開始
    setupAutoRefresh();
});

async function checkAuth() {
    try {
        const res = await fetch('/api/v1/auth/status');
        const data = await res.json();
        if (!data.isAuthenticated) {
            window.location.href = '/login.html';
        }
    } catch (e) {
        window.location.href = '/login.html';
    }
}

function setupEventListeners() {
    // ログアウト
    document.getElementById('logoutBtn').addEventListener('click', async () => {
        await fetch('/api/v1/auth/logout', { method: 'POST' });
        window.location.href = '/login.html';
    });

    // 日付変更
    document.getElementById('dateSelect').addEventListener('change', (e) => {
        updateExportDateText(e.target.value);
        refreshAllData();
    });

    // 今日ボタン
    document.getElementById('todayBtn').addEventListener('click', () => {
        const today = new Date().toISOString().split('T')[0];
        document.getElementById('dateSelect').value = today;
        updateExportDateText(today);
        refreshAllData();
    });

    // 再読み込みボタン
    document.getElementById('refreshBtn').addEventListener('click', refreshAllData);

    // 検索入力 (デバウンス)
    let searchTimeout = null;
    document.getElementById('searchInput').addEventListener('input', () => {
        clearTimeout(searchTimeout);
        searchTimeout = setTimeout(loadScans, 300);
    });

    // 自動更新チェックボックス
    document.getElementById('autoRefreshCheck').addEventListener('change', setupAutoRefresh);

    // タブ切り替え
    document.querySelectorAll('.tab-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
            document.querySelectorAll('.tab-content').forEach(c => c.style.display = 'none');

            btn.classList.add('active');
            const targetId = btn.getAttribute('data-tab');
            document.getElementById(targetId).style.display = 'block';
            currentTab = targetId;

            if (targetId === 'logsTab') loadLogs();
            if (targetId === 'unverifiedTab') loadUnverified();
        });
    });

    // 未点呼者メールコピー
    document.getElementById('copyUnverifiedEmailsBtn').addEventListener('click', () => {
        if (cachedUnverifiedEmails.length === 0) {
            alert('コピー対象のメールアドレスがありません。');
            return;
        }
        navigator.clipboard.writeText(cachedUnverifiedEmails.join(', '));
        alert(`${cachedUnverifiedEmails.length} 件のメールアドレスをクリップボードにコピーしました。`);
    });

    // ログ更新ボタン
    document.getElementById('refreshLogsBtn').addEventListener('click', loadLogs);

    // エクスポートダウンロード
    document.getElementById('downloadCsvBtn').addEventListener('click', () => {
        const date = document.getElementById('dateSelect').value;
        window.location.href = `/api/v1/dashboard/export/csv?date=${date}`;
    });

    document.getElementById('downloadBinBtn').addEventListener('click', () => {
        const date = document.getElementById('dateSelect').value;
        window.location.href = `/api/v1/dashboard/export/bin?date=${date}`;
    });
}

function updateExportDateText(dateStr) {
    document.getElementById('exportTargetDateText').innerText = dateStr || '今日';
}

function setupAutoRefresh() {
    if (autoRefreshTimer) {
        clearInterval(autoRefreshTimer);
        autoRefreshTimer = null;
    }

    const enabled = document.getElementById('autoRefreshCheck').checked;
    if (enabled) {
        autoRefreshTimer = setInterval(() => {
            if (currentTab === 'scansTab') {
                refreshAllData(true);
            }
        }, 5000);
    }
}

async function refreshAllData(isBackground = false) {
    const date = document.getElementById('dateSelect').value;
    try {
        await Promise.all([
            loadSummary(date),
            loadScans(date),
            loadUnverified(date)
        ]);

        const now = new Date();
        document.getElementById('lastUpdatedText').innerText =
            `最終更新: ${now.getHours().toString().padStart(2, '0')}:${now.getMinutes().toString().padStart(2, '0')}:${now.getSeconds().toString().padStart(2, '0')}`;
    } catch (err) {
        if (!isBackground) {
            console.error('Failed to fetch data:', err);
        }
    }
}

async function loadSummary(date) {
    const res = await fetch(`/api/v1/dashboard/summary?date=${date}`);
    if (res.status === 401) { window.location.href = '/login.html'; return; }
    if (!res.ok) return;

    const data = await res.json();

    document.getElementById('uniqueStudentsCount').innerText = data.uniqueStudentsToday;
    document.getElementById('totalScansCount').innerText = data.totalScansToday;
    document.getElementById('unverifiedStudentsCount').innerText = data.unverifiedStudentsCount;
    document.getElementById('totalMasterStudentsCount').innerText = data.totalMasterStudents;
    document.getElementById('completionRateText').innerText = data.completionRatePercentage.toFixed(1);
    document.getElementById('completionProgressBar').style.width = `${Math.min(100, data.completionRatePercentage)}%`;
    document.getElementById('unverifiedTabBadge').innerText = data.unverifiedStudentsCount;

    // 拠点別チップ表示
    const chipsContainer = document.getElementById('locationChips');
    chipsContainer.innerHTML = '';
    const locations = Object.entries(data.scansByLocation || {});
    if (locations.length === 0) {
        chipsContainer.innerHTML = '<span class="chip" style="color: var(--text-muted);">スキャンなし</span>';
    } else {
        for (const [loc, count] of locations) {
            const chip = document.createElement('span');
            chip.className = 'chip';
            chip.innerHTML = `${escapeHtml(loc)} <span class="chip-count">${escapeHtml(count)}</span>`;
            chipsContainer.appendChild(chip);
        }
    }
}

async function loadScans() {
    const date = document.getElementById('dateSelect').value;
    const search = document.getElementById('searchInput').value;

    const res = await fetch(`/api/v1/dashboard/scans?date=${date}&search=${encodeURIComponent(search)}`);
    if (res.status === 401) { window.location.href = '/login.html'; return; }
    if (!res.ok) return;

    const list = await res.json();
    const tbody = document.getElementById('scansTableBody');
    document.getElementById('scansCountBadge').innerText = list.length;

    if (list.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" style="text-align: center; color: var(--text-muted); padding: 24px;">点呼データはありません</td></tr>';
        return;
    }

    let html = '';
    for (const r of list) {
        const timeStr = r.timestamp ? new Date(r.timestamp).toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '-';
        html += `
            <tr>
                <td class="font-mono">${escapeHtml(timeStr)}</td>
                <td class="font-mono">${escapeHtml(String(r.last5).padStart(5, '0'))}</td>
                <td><span class="badge badge-info">${escapeHtml(r.studentCode || '-')}</span></td>
                <td><strong>${escapeHtml(r.studentName || '未登録')}</strong></td>
                <td><span class="badge badge-success">${escapeHtml(r.location || '未設定')}</span></td>
                <td class="font-mono" style="color: var(--text-muted);">${escapeHtml(r.barcode)}</td>
            </tr>
        `;
    }
    tbody.innerHTML = html;
}

async function loadUnverified() {
    const date = document.getElementById('dateSelect').value;
    const res = await fetch(`/api/v1/dashboard/unverified?date=${date}`);
    if (res.status === 401) { window.location.href = '/login.html'; return; }
    if (!res.ok) return;

    const list = await res.json();
    const tbody = document.getElementById('unverifiedTableBody');
    cachedUnverifiedEmails = list.map(s => s.email).filter(Boolean);

    if (list.length === 0) {
        tbody.innerHTML = '<tr><td colspan="5" style="text-align: center; color: var(--success); padding: 24px;">🎉 全員点呼完了しています！</td></tr>';
        return;
    }

    let html = '';
    for (const s of list) {
        html += `
            <tr>
                <td class="font-mono">${escapeHtml(String(s.studentNumber).padStart(5, '0'))}</td>
                <td><span class="badge badge-warning">${escapeHtml(s.code || '-')}</span></td>
                <td><strong>${escapeHtml(s.name)}</strong></td>
                <td class="font-mono" style="color: var(--text-muted);">${escapeHtml(s.email)}</td>
                <td><span class="badge badge-danger">未点呼</span></td>
            </tr>
        `;
    }
    tbody.innerHTML = html;
}

async function loadLogs() {
    const res = await fetch('/api/v1/dashboard/logs?limit=50');
    if (res.status === 401) { window.location.href = '/login.html'; return; }
    if (!res.ok) return;

    const logs = await res.json();
    const tbody = document.getElementById('logsTableBody');

    if (logs.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" style="text-align: center; color: var(--text-muted); padding: 24px;">送信ログはありません</td></tr>';
        return;
    }

    let html = '';
    for (const l of logs) {
        const timeStr = l.sentAt ? new Date(l.sentAt).toLocaleString('ja-JP') : '-';
        const resultBadge = l.isSuccess
            ? '<span class="badge badge-success">成功</span>'
            : '<span class="badge badge-danger">失敗</span>';

        html += `
            <tr>
                <td class="font-mono">${escapeHtml(timeStr)}</td>
                <td class="font-mono">${escapeHtml(String(l.studentNumber).padStart(5, '0'))}</td>
                <td class="font-mono">${escapeHtml(l.toEmail)}</td>
                <td>${resultBadge}</td>
                <td class="font-mono">${escapeHtml(l.statusCode || '-')}</td>
                <td style="color: var(--text-muted); font-size: 12px;">${escapeHtml(l.errorMessage || 'OK')}</td>
            </tr>
        `;
    }
    tbody.innerHTML = html;
}
