package dev.silksong.launcher

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder

/**
 * Keeps the app process at foreground-service priority while SetupActivity runs
 * the existing on-device pipeline. Build logic remains in its existing stages.
 */
class PortBuildService : Service() {
    companion object {
        private const val CHANNEL = "silksong_port_build"
        private const val NOTIFICATION_ID = 7314
        const val ACTION_START = "dev.silksong.launcher.PORT_BUILD_START"
        const val ACTION_UPDATE = "dev.silksong.launcher.PORT_BUILD_UPDATE"
        const val ACTION_STOP = "dev.silksong.launcher.PORT_BUILD_STOP"
        @Volatile var running: Boolean = false
            private set

        fun start(context: Context) {
            val intent = Intent(context, PortBuildService::class.java).setAction(ACTION_START)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) context.startForegroundService(intent)
            else context.startService(intent)
        }

        fun update(context: Context, title: String, percent: Int, eta: String) {
            context.startService(Intent(context, PortBuildService::class.java)
                .setAction(ACTION_UPDATE)
                .putExtra("title", title)
                .putExtra("percent", percent)
                .putExtra("eta", eta))
        }

        fun stop(context: Context) {
            context.startService(Intent(context, PortBuildService::class.java).setAction(ACTION_STOP))
        }
    }

    private lateinit var manager: NotificationManager
    private var title = "Preparing to port Silksong"
    private var percent = 0
    private var eta = "Estimating time remaining…"

    override fun onCreate() {
        super.onCreate()
        manager = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            manager.createNotificationChannel(NotificationChannel(
                CHANNEL, "Silksong porting", NotificationManager.IMPORTANCE_LOW
            ).apply { description = "Progress while building Silksong for Android" })
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START, null -> {
                running = true
                startForeground(NOTIFICATION_ID, notification())
            }
            ACTION_UPDATE -> {
                title = intent.getStringExtra("title") ?: title
                percent = intent.getIntExtra("percent", percent).coerceIn(0, 100)
                eta = intent.getStringExtra("eta") ?: eta
                if (running) manager.notify(NOTIFICATION_ID, notification())
            }
            ACTION_STOP -> {
                running = false
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
        return START_NOT_STICKY
    }

    private fun notification(): Notification {
        val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
            Notification.Builder(this, CHANNEL) else Notification.Builder(this)
        return builder
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle(title)
            .setContentText("$percent% • $eta")
            .setStyle(Notification.BigTextStyle().bigText("$percent% complete\n$eta"))
            .setProgress(100, percent, false)
            .setOngoing(true)
            .build()
    }

    override fun onBind(intent: Intent?): IBinder? = null
    override fun onDestroy() {
        running = false
        super.onDestroy()
    }
}
