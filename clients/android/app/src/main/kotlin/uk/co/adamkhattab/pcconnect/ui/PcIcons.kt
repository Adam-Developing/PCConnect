package uk.co.adamkhattab.pcconnect.ui

import androidx.annotation.DrawableRes
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import uk.co.adamkhattab.pcconnect.R

/**
 * The icons the design draws, by the name the design calls them.
 *
 * These are Material Icons Outlined as vector drawables rather than
 * `material-icons-extended`: the extended artifact is several thousand icons to
 * ship forty, and R8 only shrinks what it can prove is unused. Every glyph here
 * is one this app actually draws.
 */
object PcIcons {
    @DrawableRes val Add = R.drawable.ic_add_24
    @DrawableRes val AddAlert = R.drawable.ic_add_alert_24
    @DrawableRes val ArrowBack = R.drawable.ic_arrow_back_24
    @DrawableRes val Bedtime = R.drawable.ic_bedtime_24
    @DrawableRes val CalendarToday = R.drawable.ic_calendar_today_24
    @DrawableRes val Check = R.drawable.ic_check_24
    @DrawableRes val ChevronRight = R.drawable.ic_chevron_right_24
    @DrawableRes val Close = R.drawable.ic_close_24
    @DrawableRes val Computer = R.drawable.ic_computer_24
    @DrawableRes val ContentCopy = R.drawable.ic_content_copy_24
    @DrawableRes val Delete = R.drawable.ic_delete_24
    @DrawableRes val Devices = R.drawable.ic_devices_24
    @DrawableRes val Edit = R.drawable.ic_edit_24
    @DrawableRes val EventAvailable = R.drawable.ic_event_available_24
    @DrawableRes val EventRepeat = R.drawable.ic_event_repeat_24
    @DrawableRes val ExpandLess = R.drawable.ic_expand_less_24
    @DrawableRes val ExpandMore = R.drawable.ic_expand_more_24
    @DrawableRes val Fingerprint = R.drawable.ic_fingerprint_24
    @DrawableRes val History = R.drawable.ic_history_24
    @DrawableRes val Info = R.drawable.ic_info_24
    @DrawableRes val IosShare = R.drawable.ic_ios_share_24
    @DrawableRes val Key = R.drawable.ic_key_24
    @DrawableRes val Lock = R.drawable.ic_lock_24
    @DrawableRes val Logout = R.drawable.ic_logout_24
    @DrawableRes val NightsStay = R.drawable.ic_nights_stay_24
    @DrawableRes val Notifications = R.drawable.ic_notifications_24
    @DrawableRes val PowerSettingsNew = R.drawable.ic_power_settings_new_24
    @DrawableRes val Refresh = R.drawable.ic_refresh_24
    @DrawableRes val RestartAlt = R.drawable.ic_restart_alt_24
    @DrawableRes val Schedule = R.drawable.ic_schedule_24
    @DrawableRes val Settings = R.drawable.ic_settings_24
    @DrawableRes val Sync = R.drawable.ic_sync_24
    @DrawableRes val Visibility = R.drawable.ic_visibility_24
    @DrawableRes val VisibilityOff = R.drawable.ic_visibility_off_24

    /** The glyph the design puts on each command tile. */
    @DrawableRes
    fun forCommand(type: String): Int = when (type) {
        "lock" -> Lock
        "sleep" -> Bedtime
        "signout" -> Logout
        "hibernate" -> NightsStay
        "restart" -> RestartAlt
        "shutdown" -> PowerSettingsNew
        else -> Computer
    }
}

@Composable
fun PcIcon(
    @DrawableRes id: Int,
    contentDescription: String? = null,
    modifier: Modifier = Modifier,
    size: Dp = 20.dp,
    tint: Color = PcColors.InkSoft,
) {
    Icon(
        painter = painterResource(id),
        contentDescription = contentDescription,
        modifier = modifier.size(size),
        tint = tint,
    )
}
