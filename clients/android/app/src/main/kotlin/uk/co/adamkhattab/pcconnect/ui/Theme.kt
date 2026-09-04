package uk.co.adamkhattab.pcconnect.ui

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Typography
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * The PCConnect palette.
 *
 * One light scheme, shared with the Windows app so the two clients read as the
 * same product. Blue is the primary — buttons, the active tab, selection —
 * cyan marks a reminder occurrence, and destructive actions have a red of their
 * own so "shut down" never looks like "lock".
 *
 * There is deliberately no dark scheme. The design commits to one look on both
 * platforms; a second palette that nobody drew is a palette nobody has checked
 * the contrast of.
 */
object PcColors {
    /** Page behind the cards. */
    val Bg = Color(0xFFF8FAFC)
    val Surface = Color(0xFFFFFFFF)
    val Border = Color(0xFFE2E8F0)
    val Divider = Color(0xFFF1F5F9)
    val Track = Color(0xFFF1F5F9)

    val Ink = Color(0xFF0F172A)
    val InkSoft = Color(0xFF64748B)
    val InkFaint = Color(0xFF94A3B8)
    val InkDisabled = Color(0xFFCBD5E1)

    val Primary = Color(0xFF2563EB)
    val PrimaryTint = Color(0xFFEFF6FF)
    val Accent = Color(0xFF06B6D4)

    // Status. The dot, the icon and a filled button use the vivid value; the
    // 12px pill *text* uses the darker shade, because #22C55E and #EF4444 at
    // that size fail contrast against their own tint.
    val OnlineDot = Color(0xFF22C55E)
    val OnlineInk = Color(0xFF15803D)
    val OnlineBg = Color(0xFFDCFCE7)

    val OfflineDot = Color(0xFFEF4444)

    val Danger = Color(0xFFEF4444)
    val DangerInk = Color(0xFFB91C1C)
    val DangerBg = Color(0xFFFEE2E2)

    val WarnDot = Color(0xFFF59E0B)
    val WarnInk = Color(0xFFB45309)
    val WarnBg = Color(0xFFFEF3C7)
}

/** Corner radii, named for what they are on rather than by size. */
object PcShapes {
    val Card = RoundedCornerShape(14.dp)
    val Control = RoundedCornerShape(12.dp)
    val SmallControl = RoundedCornerShape(10.dp)
    val Tile = RoundedCornerShape(16.dp)
    val Sheet = RoundedCornerShape(topStart = 22.dp, topEnd = 22.dp)
    val Dialog = RoundedCornerShape(22.dp)
    val Pill = RoundedCornerShape(999.dp)
}

/**
 * The design is set in IBM Plex Sans with IBM Plex Mono for times and codes.
 * Neither ships with Android, and a downloadable-font provider is a network
 * dependency on the sign-in screen, so the platform families carry the same
 * scale: sizes, weights and tracking are the design's.
 */
private val Sans = FontFamily.SansSerif
internal val Mono = FontFamily.Monospace

/** Sizes lifted from the design rather than from the Material defaults. */
object PcType {
    /** "Sign in" — the one display-sized thing in the app. */
    val Display = TextStyle(fontFamily = Sans, fontSize = 30.sp, lineHeight = 34.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.6).sp)

    /** A screen's own name in the top bar. */
    val Screen = TextStyle(fontFamily = Sans, fontSize = 22.sp, lineHeight = 28.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.2).sp)

    /** A dialog or sheet heading. */
    val Heading = TextStyle(fontFamily = Sans, fontSize = 20.sp, lineHeight = 24.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.2).sp)

    /** A card's own title. */
    val CardTitle = TextStyle(fontFamily = Sans, fontSize = 17.sp, lineHeight = 22.sp, fontWeight = FontWeight.SemiBold)

    val Body = TextStyle(fontFamily = Sans, fontSize = 15.sp, lineHeight = 21.sp)
    val BodyStrong = TextStyle(fontFamily = Sans, fontSize = 15.sp, lineHeight = 21.sp, fontWeight = FontWeight.SemiBold)
    val BodySmall = TextStyle(fontFamily = Sans, fontSize = 14.sp, lineHeight = 21.sp)

    /** Secondary lines under a title. Always paired with InkFaint or InkSoft. */
    val Caption = TextStyle(fontFamily = Sans, fontSize = 12.5f.sp, lineHeight = 17.sp)

    /** A field's label, and the small strong labels inside cards. */
    val Label = TextStyle(fontFamily = Sans, fontSize = 12.5f.sp, lineHeight = 16.sp, fontWeight = FontWeight.Medium)

    /** "CONTROLS", "REMINDERS ON THIS PC". */
    val Section = TextStyle(fontFamily = Sans, fontSize = 13.sp, lineHeight = 16.sp, fontWeight = FontWeight.SemiBold, letterSpacing = 0.8.sp)

    val Chip = TextStyle(fontFamily = Sans, fontSize = 13.5f.sp, lineHeight = 18.sp, fontWeight = FontWeight.Medium)
    val Button = TextStyle(fontFamily = Sans, fontSize = 15.5f.sp, lineHeight = 20.sp, fontWeight = FontWeight.SemiBold)
    val NavLabel = TextStyle(fontFamily = Sans, fontSize = 12.sp, lineHeight = 15.sp, fontWeight = FontWeight.Medium)

    /** Times, pairing codes and log lines line up under each other. */
    val MonoTime = TextStyle(fontFamily = Mono, fontSize = 13.5f.sp, lineHeight = 18.sp)
    val MonoSmall = TextStyle(fontFamily = Mono, fontSize = 12.sp, lineHeight = 16.sp)
}

private val Scheme = lightColorScheme(
    primary = PcColors.Primary,
    onPrimary = Color.White,
    primaryContainer = PcColors.PrimaryTint,
    onPrimaryContainer = PcColors.Primary,
    secondary = PcColors.InkSoft,
    onSecondary = Color.White,
    tertiary = PcColors.Accent,
    background = PcColors.Bg,
    onBackground = PcColors.Ink,
    surface = PcColors.Surface,
    onSurface = PcColors.Ink,
    surfaceVariant = PcColors.Track,
    onSurfaceVariant = PcColors.InkSoft,
    outline = PcColors.Border,
    outlineVariant = PcColors.Divider,
    error = PcColors.Danger,
    onError = Color.White,
    errorContainer = PcColors.DangerBg,
    onErrorContainer = PcColors.DangerInk,
    scrim = Color(0xFF0F172A),
)

/**
 * Material's own type scale, restated in the design's sizes, so the few places
 * that fall through to `MaterialTheme.typography` land on the same scale as the
 * places that name a [PcType] style.
 */
private val PcTypography = Typography(
    headlineLarge = PcType.Display,
    headlineMedium = PcType.Screen,
    headlineSmall = PcType.Heading,
    titleLarge = PcType.Heading,
    titleMedium = PcType.CardTitle,
    titleSmall = PcType.BodyStrong,
    bodyLarge = PcType.Body,
    bodyMedium = PcType.BodySmall,
    bodySmall = PcType.Caption,
    labelLarge = PcType.Chip,
    labelMedium = PcType.Label,
    labelSmall = PcType.MonoSmall,
)

@Composable
fun PcConnectTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = Scheme, typography = PcTypography) {
        // Surface is what makes LocalContentColor follow the scheme. Without it
        // every heading outside a Scaffold keeps Compose's default black.
        Surface(color = PcColors.Bg, contentColor = PcColors.Ink, content = content)
    }
}
