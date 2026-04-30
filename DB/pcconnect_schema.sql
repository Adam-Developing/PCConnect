-- phpMyAdmin SQL Dump
-- version 5.2.1
-- https://www.phpmyadmin.net/
--
-- Host: localhost
-- Generation Time: Apr 16, 2026 at 01:58 AM
-- Server version: 8.0.36
-- PHP Version: 8.3.15

SET SQL_MODE = "NO_AUTO_VALUE_ON_ZERO";
START TRANSACTION;
SET time_zone = "+00:00";


/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!40101 SET NAMES utf8mb4 */;

--
-- Database: `pcconnect`
--

-- --------------------------------------------------------

--
-- Table structure for table `apikeys`
--

CREATE TABLE `apikeys` (
  `username` varchar(255) NOT NULL,
  `api_key` varchar(512) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `code`
--

CREATE TABLE `code` (
  `ID` int NOT NULL,
  `Code` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `feedback`
--

CREATE TABLE `feedback` (
  `FeedbackID` int NOT NULL,
  `Name` text NOT NULL,
  `Email` text NOT NULL,
  `Feedback` text NOT NULL,
  `Rating` text NOT NULL,
  `IP` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `links`
--

CREATE TABLE `links` (
  `ID` int NOT NULL,
  `Name` text NOT NULL,
  `URL` text NOT NULL,
  `sort_order` int NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `mailing_list`
--

CREATE TABLE `mailing_list` (
  `ID` int NOT NULL,
  `UserID` int NOT NULL DEFAULT '0',
  `Email` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `menupages`
--

CREATE TABLE `menupages` (
  `ID` int NOT NULL,
  `Name` text NOT NULL,
  `URL` text NOT NULL,
  `sort_order` int NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `pcnames`
--

CREATE TABLE `pcnames` (
  `PCID` int NOT NULL,
  `Username` text NOT NULL,
  `PCName` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `reminders`
--

CREATE TABLE `reminders` (
  `ID` int NOT NULL,
  `Username` text NOT NULL,
  `Time` time NOT NULL,
  `Reminder` text NOT NULL,
  `Completed` int NOT NULL DEFAULT '0',
  `Recurrence` varchar(255) DEFAULT 'none',
  `Recurrence_Frequency` varchar(255) DEFAULT NULL,
  `Recurrence_Day` varchar(255) DEFAULT NULL,
  `Recurrence_Time` time DEFAULT NULL,
  `Recurrence_End_Date` date DEFAULT NULL,
  `Date` date DEFAULT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `requests`
--

CREATE TABLE `requests` (
  `ID` int NOT NULL,
  `Username` text NOT NULL,
  `PCName` text CHARACTER SET utf8mb3 COLLATE utf8mb3_general_ci,
  `Request` text NOT NULL,
  `Value` int NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `time`
--

CREATE TABLE `time` (
  `ID` int NOT NULL,
  `Username` text NOT NULL,
  `PCName` text CHARACTER SET utf8mb3 COLLATE utf8mb3_general_ci,
  `Time` text
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `users`
--

CREATE TABLE `users` (
  `id` int NOT NULL,
  `Name` text NOT NULL,
  `Username` varchar(50) NOT NULL,
  `DateOfBirth` text NOT NULL,
  `Email` text NOT NULL,
  `Password` varchar(64) NOT NULL,
  `Enabled` int NOT NULL DEFAULT '1',
  `DateTimeOfSignup` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `verifications`
--

CREATE TABLE `verifications` (
  `ID` int NOT NULL,
  `TypeID` int NOT NULL,
  `Code` text NOT NULL,
  `Expiry` datetime NOT NULL,
  `Current` text NOT NULL,
  `UserID` int NOT NULL,
  `IP` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


-- --------------------------------------------------------

--
-- Table structure for table `verificationtypes`
--

CREATE TABLE `verificationtypes` (
  `TypeID` int NOT NULL,
  `Type` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;

--


--
-- Indexes for dumped tables
--

--
-- Indexes for table `apikeys`
--
ALTER TABLE `apikeys`
  ADD PRIMARY KEY (`username`),
  ADD UNIQUE KEY `username` (`username`);

--
-- Indexes for table `code`
--
ALTER TABLE `code`
  ADD PRIMARY KEY (`ID`);

--
-- Indexes for table `feedback`
--
ALTER TABLE `feedback`
  ADD PRIMARY KEY (`FeedbackID`);

--
-- Indexes for table `links`
--
ALTER TABLE `links`
  ADD PRIMARY KEY (`ID`);

--
-- Indexes for table `mailing_list`
--
ALTER TABLE `mailing_list`
  ADD PRIMARY KEY (`ID`);

--
-- Indexes for table `menupages`
--
ALTER TABLE `menupages`
  ADD PRIMARY KEY (`ID`);

--
-- Indexes for table `pcnames`
--
ALTER TABLE `pcnames`
  ADD PRIMARY KEY (`PCID`);

--
-- Indexes for table `reminders`
--
ALTER TABLE `reminders`
  ADD PRIMARY KEY (`ID`);

--
-- Indexes for table `requests`
--
ALTER TABLE `requests`
  ADD PRIMARY KEY (`ID`);

--
-- Indexes for table `time`
--
ALTER TABLE `time`
  ADD PRIMARY KEY (`ID`);

--
-- Indexes for table `users`
--
ALTER TABLE `users`
  ADD PRIMARY KEY (`id`);

--
-- Indexes for table `verifications`
--
ALTER TABLE `verifications`
  ADD PRIMARY KEY (`ID`),
  ADD KEY `TypeID` (`TypeID`);

--
-- Indexes for table `verificationtypes`
--
ALTER TABLE `verificationtypes`
  ADD PRIMARY KEY (`TypeID`);

--
-- AUTO_INCREMENT for dumped tables
--

--
-- AUTO_INCREMENT for table `code`
--
ALTER TABLE `code`
  MODIFY `ID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=4;

--
-- AUTO_INCREMENT for table `feedback`
--
ALTER TABLE `feedback`
  MODIFY `FeedbackID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=7;

--
-- AUTO_INCREMENT for table `links`
--
ALTER TABLE `links`
  MODIFY `ID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=12;

--
-- AUTO_INCREMENT for table `mailing_list`
--
ALTER TABLE `mailing_list`
  MODIFY `ID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=1739;

--
-- AUTO_INCREMENT for table `pcnames`
--
ALTER TABLE `pcnames`
  MODIFY `PCID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=133;

--
-- AUTO_INCREMENT for table `reminders`
--
ALTER TABLE `reminders`
  MODIFY `ID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=1810;

--
-- AUTO_INCREMENT for table `requests`
--
ALTER TABLE `requests`
  MODIFY `ID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=1873;

--
-- AUTO_INCREMENT for table `time`
--
ALTER TABLE `time`
  MODIFY `ID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=1870;

--
-- AUTO_INCREMENT for table `users`
--
ALTER TABLE `users`
  MODIFY `id` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=1775;

--
-- AUTO_INCREMENT for table `verifications`
--
ALTER TABLE `verifications`
  MODIFY `ID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=24;

--
-- AUTO_INCREMENT for table `verificationtypes`
--
ALTER TABLE `verificationtypes`
  MODIFY `TypeID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=2;
COMMIT;

/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
